namespace MassTransit.NHibernateIntegration.Saga
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Context;
    using GreenPipes;
    using MassTransit.Saga;
    using Metadata;
    using NHibernate;
    using NHibernate.Exceptions;
    using Util;


    public class NHibernateSagaRepository<TSaga> :
        ISagaRepository<TSaga>,
        IQuerySagaRepository<TSaga>
        where TSaga : class, ISaga
    {
        readonly ISessionFactory _sessionFactory;

        public NHibernateSagaRepository(ISessionFactory sessionFactory)
        {
            _sessionFactory = sessionFactory;
        }

        public NHibernateSagaRepository(ISessionFactory sessionFactory, System.Data.IsolationLevel isolationLevel)
        {
            _sessionFactory = sessionFactory;
        }

        public async Task<IEnumerable<Guid>> Find(ISagaQuery<TSaga> query)
        {
            using (var session = _sessionFactory.OpenSession())
            {
                return await session.QueryOver<TSaga>()
                    .Where(query.FilterExpression)
                    .Select(x => x.CorrelationId)
                    .ListAsync<Guid>().ConfigureAwait(false);
            }
        }

        void IProbeSite.Probe(ProbeContext context)
        {
            var scope = context.CreateScope("sagaRepository");
            scope.Set(new
            {
                Persistence = "nhibernate",
                Entities = _sessionFactory.GetAllClassMetadata().Select(x => x.Value.EntityName).ToArray()
            });
        }

        async Task ISagaRepository<TSaga>.Send<T>(ConsumeContext<T> context, ISagaPolicy<TSaga, T> policy, IPipe<SagaConsumeContext<TSaga, T>> next)
        {
            if (!context.CorrelationId.HasValue)
                throw new SagaException("The CorrelationId was not specified", typeof(TSaga), typeof(T));

            var sagaId = context.CorrelationId.Value;

            using (var session = _sessionFactory.OpenSession())
            using (var transaction = session.BeginTransaction())
            {
                var inserted = false;

                if (policy.PreInsertInstance(context, out var instance))
                    inserted = await PreInsertSagaInstance<T>(session, instance).ConfigureAwait(false);

                try
                {
                    if (instance == null)
                        instance = await session.GetAsync<TSaga>(sagaId, LockMode.Upgrade).ConfigureAwait(false);
                    if (instance == null)
                    {
                        var missingSagaPipe = new MissingPipe<T>(session, next);

                        await policy.Missing(context, missingSagaPipe).ConfigureAwait(false);
                    }
                    else
                    {
                        LogContext.Debug?.Log("SAGA:{SagaType}:{CorrelationId} Used {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                            instance.CorrelationId, TypeMetadataCache<T>.ShortName);

                        var sagaConsumeContext = new NHibernateSagaConsumeContext<TSaga, T>(session, context, instance);

                        await policy.Existing(sagaConsumeContext, next).ConfigureAwait(false);

                        if (inserted && !sagaConsumeContext.IsCompleted)
                            await session.UpdateAsync(instance).ConfigureAwait(false);
                    }

                    if (transaction.IsActive)
                        await transaction.CommitAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogContext.Error?.Log(ex, "SAGA:{SagaType}:{CorrelationId} Exception {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                        instance?.CorrelationId, TypeMetadataCache<T>.ShortName);

                    if (transaction.IsActive)
                        await transaction.RollbackAsync().ConfigureAwait(false);

                    throw;
                }
            }
        }

        public async Task SendQuery<T>(SagaQueryConsumeContext<TSaga, T> context, ISagaPolicy<TSaga, T> policy, IPipe<SagaConsumeContext<TSaga, T>> next)
            where T : class
        {
            using (var session = _sessionFactory.OpenSession())
            using (var transaction = session.BeginTransaction())
            {
                try
                {
                    IList<TSaga> instances = await session.QueryOver<TSaga>()
                        .Where(context.Query.FilterExpression)
                        .ListAsync<TSaga>()
                        .ConfigureAwait(false);

                    if (instances.Count == 0)
                    {
                        var missingSagaPipe = new MissingPipe<T>(session, next);
                        await policy.Missing(context, missingSagaPipe).ConfigureAwait(false);
                    }
                    else
                        await Task.WhenAll(instances.Select(instance => SendToInstance(context, policy, instance, next, session))).ConfigureAwait(false);

                    // TODO partial failure should not affect them all

                    if (transaction.IsActive)
                        await transaction.CommitAsync().ConfigureAwait(false);
                }
                catch (SagaException sex)
                {
                    LogContext.Error?.Log(sex, "SAGA:{SagaType} Exception {MessageType}", TypeMetadataCache<TSaga>.ShortName, TypeMetadataCache<T>.ShortName);

                    if (transaction.IsActive)
                        await transaction.RollbackAsync().ConfigureAwait(false);

                    throw;
                }
                catch (Exception ex)
                {
                    LogContext.Error?.Log(ex, "SAGA:{SagaType} Exception {MessageType}", TypeMetadataCache<TSaga>.ShortName, TypeMetadataCache<T>.ShortName);

                    if (transaction.IsActive)
                        await transaction.RollbackAsync().ConfigureAwait(false);

                    throw new SagaException(ex.Message, typeof(TSaga), typeof(T), Guid.Empty, ex);
                }
            }
        }

        static async Task<bool> PreInsertSagaInstance<T>(ISession session, TSaga instance)
        {
            bool inserted = false;

            try
            {
                await session.SaveAsync(instance).ConfigureAwait(false);
                await session.FlushAsync().ConfigureAwait(false);

                inserted = true;

                LogContext.Debug?.Log("SAGA:{SagaType}:{CorrelationId} Insert {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                    instance.CorrelationId, TypeMetadataCache<T>.ShortName);
            }
            catch (GenericADOException ex)
            {
                LogContext.Debug?.Log(ex, "SAGA:{SagaType}:{CorrelationId} Dupe {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                    instance.CorrelationId, TypeMetadataCache<T>.ShortName);
            }

            return inserted;
        }

        static Task SendToInstance<T>(SagaQueryConsumeContext<TSaga, T> context, ISagaPolicy<TSaga, T> policy, TSaga instance,
            IPipe<SagaConsumeContext<TSaga, T>> next, ISession session)
            where T : class
        {
            try
            {
                LogContext.Debug?.Log("SAGA:{SagaType}:{CorrelationId} Used {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                    instance.CorrelationId, TypeMetadataCache<T>.ShortName);

                var sagaConsumeContext = new NHibernateSagaConsumeContext<TSaga, T>(session, context, instance);

                return policy.Existing(sagaConsumeContext, next);
            }
            catch (SagaException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SagaException(ex.Message, typeof(TSaga), typeof(T), instance.CorrelationId, ex);
            }
        }


        /// <summary>
        /// Once the message pipe has processed the saga instance, add it to the saga repository
        /// </summary>
        /// <typeparam name="TMessage"></typeparam>
        class MissingPipe<TMessage> :
            IPipe<SagaConsumeContext<TSaga, TMessage>>
            where TMessage : class
        {
            readonly IPipe<SagaConsumeContext<TSaga, TMessage>> _next;
            readonly ISession _session;

            public MissingPipe(ISession session, IPipe<SagaConsumeContext<TSaga, TMessage>> next)
            {
                _session = session;
                _next = next;
            }

            void IProbeSite.Probe(ProbeContext context)
            {
                _next.Probe(context);
            }

            public async Task Send(SagaConsumeContext<TSaga, TMessage> context)
            {
                LogContext.Debug?.Log("SAGA:{SagaType}:{CorrelationId} Added {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                    context.Saga.CorrelationId, TypeMetadataCache<TMessage>.ShortName);

                SagaConsumeContext<TSaga, TMessage> proxy = new NHibernateSagaConsumeContext<TSaga, TMessage>(_session, context, context.Saga);

                await _next.Send(proxy).ConfigureAwait(false);

                if (!proxy.IsCompleted)
                    await _session.SaveAsync(context.Saga).ConfigureAwait(false);
            }
        }
    }
}
