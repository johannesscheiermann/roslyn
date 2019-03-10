﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.SolutionCrawler
{
    internal abstract class IdleProcessor
    {
        private const int MinimumDelayInMS = 50;

        protected readonly IAsynchronousOperationListener Listener;
        protected readonly CancellationToken CancellationToken;
        protected readonly int BackOffTimeSpanInMS;

        // points to processor task
        private Task _processorTask;

        // there is one thread that writes to it and one thread reads from it
        private int _lastAccessTimeInMS;

        public IdleProcessor(
            IAsynchronousOperationListener listener,
            int backOffTimeSpanInMS,
            CancellationToken cancellationToken)
        {
            this.Listener = listener;
            this.CancellationToken = cancellationToken;

            BackOffTimeSpanInMS = backOffTimeSpanInMS;
            _lastAccessTimeInMS = Environment.TickCount;
        }

        protected abstract Task WaitAsync(CancellationToken cancellationToken);
        protected abstract Task ExecuteAsync();

        protected void Start()
        {
            if (_processorTask == null)
            {
                _processorTask = Task.Factory.SafeStartNewFromAsync(ProcessAsync, this.CancellationToken, TaskScheduler.Default);
            }
        }

        protected void UpdateLastAccessTime()
        {
            _lastAccessTimeInMS = Environment.TickCount;
        }

        protected async Task WaitForIdleAsync(INotifyBlockedOnCompletion notifyBlockedOnCompletion)
        {
            while (true)
            {
                if (this.CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var diffInMS = Environment.TickCount - _lastAccessTimeInMS;
                if (diffInMS >= BackOffTimeSpanInMS)
                {
                    return;
                }

                var blockedOnCompletion = notifyBlockedOnCompletion.BlockedOnCompletion;
                using (var combinedCancellationToken = CancellationToken.CombineWith(blockedOnCompletion))
                {
                    try
                    {
                        // TODO: will safestart/unwarp capture cancellation exception?
                        var timeLeft = BackOffTimeSpanInMS - diffInMS;
                        await Task.Delay(Math.Max(MinimumDelayInMS, timeLeft), combinedCancellationToken.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (blockedOnCompletion.IsCancellationRequested && !CancellationToken.IsCancellationRequested)
                    {
                        // The delay terminated early to accommodate a blocking operation. Make sure to delay long
                        // enough that low priority (on idle) operations get a chance to be triggered.
                        await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
                        return;
                    }
                }
            }
        }

        private async Task ProcessAsync()
        {
            while (true)
            {
                try
                {
                    if (this.CancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // wait for next item available
                    await WaitAsync(this.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                    using (var asyncToken = this.Listener.BeginAsyncOperation("ProcessAsync"))
                    {
                        // we have items but workspace is busy. wait for idle.
                        await WaitForIdleAsync(asyncToken).ConfigureAwait(continueOnCapturedContext: false);

                        await ExecuteAsync().ConfigureAwait(continueOnCapturedContext: false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignore cancellation exception
                }
            }
        }

        public virtual Task AsyncProcessorTask
        {
            get
            {
                if (_processorTask == null)
                {
                    return Task.CompletedTask;
                }

                return _processorTask;
            }
        }
    }
}
