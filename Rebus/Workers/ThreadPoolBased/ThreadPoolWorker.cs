﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Threading;
using Rebus.Transport;

namespace Rebus.Workers.ThreadPoolBased
{
    class ThreadPoolWorker : IWorker
    {
        readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        readonly ITransport _transport;
        readonly IPipelineInvoker _pipelineInvoker;
        readonly ParallelOperationsManager _parallelOperationsManager;
        readonly RebusBus _owningBus;
        readonly Options _options;
        readonly ISyncBackoffStrategy _backoffStrategy;
        readonly Thread _workerThread;
        readonly ILog _log;

        internal ThreadPoolWorker(string name, ITransport transport, IRebusLoggerFactory rebusLoggerFactory, IPipelineInvoker pipelineInvoker, ParallelOperationsManager parallelOperationsManager, RebusBus owningBus, Options options, ISyncBackoffStrategy backoffStrategy)
        {
            Name = name;
            _log = rebusLoggerFactory.GetLogger<ThreadPoolWorker>();
            _transport = transport;
            _pipelineInvoker = pipelineInvoker;
            _parallelOperationsManager = parallelOperationsManager;
            _owningBus = owningBus;
            _options = options;
            _backoffStrategy = backoffStrategy;
            _workerThread = new Thread(Run)
            {
                Name = name,
                IsBackground = true
            };
            _workerThread.Start();
        }

        public string Name { get; }

        void Run()
        {
            _log.Debug("Starting (threadpool-based) worker {workerName}", Name);

            var token = _cancellationTokenSource.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    TryReceiveNextMessage(token);
                }
                catch (Exception exception)
                {
                    _log.Error(exception, "Unhandled exception in worker!!");

                    _backoffStrategy.WaitError();
                }
            }

            _log.Debug("Worker {workerName} stopped", Name);
        }

        void TryReceiveNextMessage(CancellationToken token)
        {
            var parallelOperation = _parallelOperationsManager.TryBegin();

            if (!parallelOperation.CanContinue())
            {
                _backoffStrategy.Wait();
                return;
            }

            TryAsyncReceive(token, parallelOperation);
        }

        async void TryAsyncReceive(CancellationToken token, IDisposable parallelOperation)
        {
            try
            {
                using (parallelOperation)
                using (var context = new TransactionContext())
                {
                    var transportMessage = await ReceiveTransportMessage(token, context);

                    if (transportMessage == null)
                    {
                        context.Dispose();

                        // no need for another thread to rush in and discover that there is no message
                        //parallelOperation.Dispose();

                        _backoffStrategy.WaitNoMessage();
                        return;
                    }

                    _backoffStrategy.Reset();

                    await ProcessMessage(context, transportMessage);
                }
            }
            catch (TaskCanceledException)
            {
                // it's fine - just a sign that we are shutting down
            }
            catch (OperationCanceledException)
            {
                // it's fine - just a sign that we are shutting down
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Unhandled exception in thread pool worker");
            }
        }

        async Task<TransportMessage> ReceiveTransportMessage(CancellationToken token, ITransactionContext context)
        {
            try
            {
                return await _transport.Receive(context, token);
            }
            catch (TaskCanceledException)
            {
                // it's fine - just a sign that we are shutting down
                throw;
            }
            catch (OperationCanceledException)
            {
                // it's fine - just a sign that we are shutting down
                throw;
            }
            catch (Exception exception)
            {
                _log.Warn("An error occurred when attempting to receive the next message: {exception}", exception);

                _backoffStrategy.WaitError();

                return null;
            }
        }

        async Task ProcessMessage(TransactionContext context, TransportMessage transportMessage)
        {
            try
            {
                context.Items["OwningBus"] = _owningBus;

                AmbientTransactionContext.SetCurrent(context);

                var stepContext = new IncomingStepContext(transportMessage, context);
                await _pipelineInvoker.Invoke(stepContext);

                try
                {
                    await context.Complete();
                }
                catch (Exception exception)
                {
                    _log.Error(exception, "An error occurred when attempting to complete the transaction context");
                }
            }
            catch (ThreadAbortException exception)
            {
                context.Abort();

                _log.Error(exception, "Worker was killed while handling message {messageLabel}", transportMessage.GetMessageLabel());
            }
            catch (Exception exception)
            {
                context.Abort();

                _log.Error(exception, "Unhandled exception while handling message {messageLabel}", transportMessage.GetMessageLabel());
            }
            finally
            {
                AmbientTransactionContext.SetCurrent(null);
            }
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
        }

        public void Dispose()
        {
            Stop();

            if (!_workerThread.Join(_options.WorkerShutdownTimeout))
            {
                _log.Warn("The {workerName} worker did not shut down within {shutdownTimeoutSeconds} seconds!", _options.WorkerShutdownTimeout.TotalSeconds);

                _workerThread.Abort();
            }
        }
    }
}