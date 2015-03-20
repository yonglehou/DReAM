/*
 * MindTouch Dream - a distributed REST framework 
 * Copyright (C) 2006-2014 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MindTouch.Dream;
using MindTouch.IO;
using MindTouch.Threading;

namespace MindTouch.Tasking {
    using Yield = IEnumerator<IYield>;

    /// <summary>
    /// Thrown when completion of all of a list of alternate synchronization handles fails.
    /// </summary>
    /// <remarks>Used by <see cref="AResultEx.Alt"/> and <see cref="AResultEx.Alt{T}"/>.</remarks>
    /// <seealso cref="AResultEx.Alt"/>
    /// <seealso cref="AResultEx.Alt{T}"/>
    public class AsyncAllAlternatesFailed : Exception { }

    /// <summary>
    /// Data structure capturing the thread and its custom information object.
    /// </summary>
    public struct ThreadInfo {

        //--- Fields ---
        public readonly Thread Thread;
        public readonly object Info;

        //--- Contructors ---
        public ThreadInfo(Thread thread, object info) {
            this.Thread = thread;
            this.Info = info;
        }
    }

    /// <summary>
    /// Static utility class containing extension and helper methods for handling asynchronous execution.
    /// </summary>
    public static class AsyncUtil {

        //--- Types ---
        private delegate void AvailableThreadsDelegate(out int availableThreads, out int availablePorts);
        private delegate void AvailableBackgroundThreadsDelegate(out int availableThreads);

        private class BoxedObject {
            
            //--- Fields ---
            public volatile object Value;
        }

        //--- Class Fields ---

        /// <summary>
        /// The globally accessible <see cref="IDispatchQueue"/> for dispatching work without queue affinity.
        /// </summary>
        public static readonly IDispatchQueue GlobalDispatchQueue;

        /// <summary>
        /// The <see cref="IDispatchQueue"/> for dispatching background work without queue affinity.
        /// </summary>
        private static readonly IDispatchQueue _backgroundDispatchQueue;
        private static readonly log4net.ILog _log = LogUtils.CreateLog();
        private static bool _inplaceActivation = true;
        private static readonly int _minThreads;
        private static readonly int _maxThreads;
        private static readonly int _minPorts;
        private static readonly int _maxPorts;
        private static readonly int _minBackgroundThreads;
        private static readonly int _maxBackgroundThreads;
        private static readonly AvailableThreadsDelegate _availableThreadsCallback;
        private static readonly AvailableBackgroundThreadsDelegate _availableBackgroundThreadsCallback;
        private static readonly int? _maxStackSize;
        private static readonly Dictionary<int, KeyValuePair<Thread, BoxedObject>> _threads = new Dictionary<int, KeyValuePair<Thread, BoxedObject>>();

        [ThreadStatic]
        private static IDispatchQueue _currentDispatchQueue;

        [ThreadStatic]
        private static BoxedObject _threadInfo;

        //--- Constructors ---
        static AsyncUtil() {

            // Global Thread Pool
            if(!int.TryParse(System.Configuration.ConfigurationManager.AppSettings["threadpool-min"], out _minThreads)) {
                _minThreads = 4;
            }
            if(!int.TryParse(System.Configuration.ConfigurationManager.AppSettings["threadpool-max"], out _maxThreads)) {
                _maxThreads = 200;
            }
            int maxStackSize;
            if(int.TryParse(System.Configuration.ConfigurationManager.AppSettings["max-stacksize"], out maxStackSize)) {
                _maxStackSize = maxStackSize;
            }

            // Background Thread Pool
            if(!int.TryParse(System.Configuration.ConfigurationManager.AppSettings["background-threadpool-min"], out _minBackgroundThreads)) {
                _minBackgroundThreads = 4;
            }
            if(!int.TryParse(System.Configuration.ConfigurationManager.AppSettings["background-threadpool-max"], out _maxBackgroundThreads)) {
                _maxBackgroundThreads = 20;
            }

            // check which global dispatch queue implementation to use
            int dummy;
            switch(System.Configuration.ConfigurationManager.AppSettings["threadpool"]) {
            default:
            case "elastic":
                ThreadPool.GetMinThreads(out dummy, out _minPorts);
                ThreadPool.GetMaxThreads(out dummy, out _maxPorts);
                _log.DebugFormat("Using Global ElasticThreadPool with {0}min / {1}max", _minThreads, _maxThreads);
                _log.DebugFormat("Using Background ElasticThreadPool with {0}min / {1}max", _minBackgroundThreads, _maxBackgroundThreads);
                var elasticThreadPool = new ElasticThreadPool(_minThreads, _maxThreads);
                GlobalDispatchQueue = elasticThreadPool;
                var backgroundThreadPool = new ElasticThreadPool(_minBackgroundThreads, _maxBackgroundThreads);
                _backgroundDispatchQueue = backgroundThreadPool;
                _inplaceActivation = false;
                _availableThreadsCallback = delegate(out int threads, out int ports) {
                    int dummy2;
                    ThreadPool.GetAvailableThreads(out dummy2, out ports);
                    threads = elasticThreadPool.MaxParallelThreads - elasticThreadPool.ThreadCount;
                };
                _availableBackgroundThreadsCallback = delegate(out int threads) {
                    threads = backgroundThreadPool.MaxParallelThreads - backgroundThreadPool.ThreadCount;
                };
                break;
            case "legacy":
                ThreadPool.GetMinThreads(out dummy, out _minPorts);
                ThreadPool.GetMaxThreads(out dummy, out _maxPorts);
                ThreadPool.SetMinThreads(_minThreads, _minPorts);
                ThreadPool.SetMaxThreads(_maxThreads, _maxPorts);
                _log.Debug("Using LegacyThreadPool");
                GlobalDispatchQueue = LegacyThreadPool.Instance;
                _backgroundDispatchQueue = LegacyThreadPool.Instance;
                _availableThreadsCallback = ThreadPool.GetAvailableThreads;
                break;
            }
        }

        //--- Class Properties ---

        /// <summary>
        /// The <see cref="IDispatchQueue"/> used by the current execution environment.
        /// </summary>
        public static IDispatchQueue CurrentDispatchQueue {
            get {
                return _currentDispatchQueue ?? (_inplaceActivation ? ImmediateDispatchQueue.Instance : GlobalDispatchQueue);
            }
            set {
                _currentDispatchQueue = value;
            }
        }

        /// <summary>
        /// The maximum stack size that Threads created by Dream (<see cref="ElasticThreadPool"/>, <see cref="Fork(System.Action)"/>, <see cref="CreateThread"/>, etc.)
        /// should use. If null, uses process default stack size.
        /// </summary>
        public static int? MaxStackSize {
            get { return _maxStackSize; }
        }

        /// <summary>
        /// Enumerate all created threads.
        /// </summary>
        public static IEnumerable<ThreadInfo> Threads {
            get {
                lock(_threads) {
                    return _threads.Values.Select(kv => new ThreadInfo(kv.Key, kv.Value.Value)).ToArray();
                }
            }
        }

        /// <summary>
        /// Data associated with current thread.
        /// </summary>
        public static object ThreadInfo {
            get { return (_threadInfo != null) ? _threadInfo.Value : null; }
            set {
                if(_threadInfo != null) {
                    _threadInfo.Value = value;
                }
            }
        }

        //--- Class Methods ---

        /// <summary>
        /// Get the maximum number of resources allowed for this process' execution environment.
        /// </summary>
        /// <param name="threads">Number of threads allowed.</param>
        /// <param name="ports">Number of completion ports allowed.</param>
        /// <param name="dispatchers">Number of dispatchers allowed.</param>
        /// <param name="backgroundThreads">Number of background threads allowed.</param>
        public static void GetMaxThreads(out int threads, out int ports, out int dispatchers, out int backgroundThreads) {
            threads = _maxThreads;
            ports = _maxPorts;
            dispatchers = DispatchThreadScheduler.MaxThreadCount;
            backgroundThreads = _maxBackgroundThreads;
        }

        /// <summary>
        /// Get the minimum number of resources allocated forthis process' execution environment.
        /// </summary>
        /// <param name="threads">Minimum number of threads allocated.</param>
        /// <param name="ports">Minimum number of completion ports allocated.</param>
        /// <param name="dispatchers">Minimum number of dispatchers allocated.</param>
        /// <param name="backgroundThreads">Minimum number of background threads allocated.</param>
        public static void GetAvailableThreads(out int threads, out int ports, out int dispatchers, out int backgroundThreads) {
            _availableThreadsCallback(out threads, out ports);
            dispatchers = DispatchThreadScheduler.AvailableThreadCount;
            _availableBackgroundThreadsCallback(out backgroundThreads);
        }

        /// <summary>
        /// Dispatch an action to be executed via the <see cref="GlobalDispatchQueue"/>.
        /// </summary>
        /// <param name="handler">Action to enqueue for execution.</param>
        public static void Fork(Action handler) {
            GlobalDispatchQueue.QueueWorkItemWithClonedEnv(handler, null);
        }

        /// <summary>
        /// Dispatch an action to be executed via the <see cref="GlobalDispatchQueue"/>.
        /// </summary>
        /// <param name="handler">Action to enqueue for execution.</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle for the action's execution.</returns>
        public static Result Fork(Action handler, Result result) {
            return GlobalDispatchQueue.QueueWorkItemWithClonedEnv(handler, result);
        }

        /// <summary>
        /// Dispatch an action to be executed via the <see cref="GlobalDispatchQueue"/>.
        /// </summary>
        /// <param name="handler">Action to enqueue for execution.</param>
        /// <param name="env">Environment in which to execute the action.</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle for the action's execution.</returns>
        public static Result Fork(Action handler, TaskEnv env, Result result) {
            return GlobalDispatchQueue.QueueWorkItemWithEnv(handler, env, result);
        }

        /// <summary>
        /// Dispatch an action to be executed via the <see cref="GlobalDispatchQueue"/>.
        /// </summary>
        /// <typeparam name="T">Type of result value produced by action.</typeparam>
        /// <param name="handler">Action to enqueue for execution.</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle for the action's execution.</returns>
        public static Result<T> Fork<T>(Func<T> handler, Result<T> result) {
            return GlobalDispatchQueue.QueueWorkItemWithClonedEnv(handler, result);
        }

        /// <summary>
        /// Dispatch an action to be executed in the background via the <see cref="_backgroundDispatchQueue"/>.
        /// </summary>
        /// <param name="handler">Action to enqueue for execution.</param>
        public static void ForkBackgroundSender(Action handler) {
            _backgroundDispatchQueue.QueueWorkItemWithEnv(handler, TaskEnv.New(_backgroundDispatchQueue), null);
        }

        /// <summary>
        /// Dispatch an action to be executed with a new, dedicated backgrouns thread.
        /// </summary>
        /// <param name="handler">Action to enqueue for execution.</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle for the action's execution.</returns>
        public static Result ForkThread(Action handler, Result result) {
            CreateThread(TaskEnv.Clone().MakeAction(handler, result)).Start();
            return result;
        }

        /// <summary>
        /// Dispatch an action to be executed with a new, dedicated backgrouns thread.
        /// </summary>
        /// <typeparam name="T">Type of result value produced by action.</typeparam>
        /// <param name="handler">Action to enqueue for execution.</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle for the action's execution.</returns>
        public static Result<T> ForkThread<T>(Func<T> handler, Result<T> result) {
            CreateThread(TaskEnv.Clone().MakeAction(handler, result)).Start();
            return result;
        }

        /// <summary>
        /// Dispatch an action to be executed with a new, dedicated backgrouns thread.
        /// </summary>
        /// <param name="handler">Action to enqueue for execution.</param>
        public static Thread CreateThread(Action handler) {
            ThreadStart threadStart = () => {

                // initialize thread data
                _threadInfo = new BoxedObject();
                lock(_threads) {
                    _threads[Thread.CurrentThread.ManagedThreadId] = new KeyValuePair<Thread, BoxedObject>(Thread.CurrentThread, _threadInfo);
                }

                // run thread
                try {
                    handler();
                } finally {

                    // clean-up thread data
                    lock(_threads) {
                        _threads.Remove(Thread.CurrentThread.ManagedThreadId);
                    }
                    _threadInfo = null;
                }
            };
            return MaxStackSize.HasValue
                ? new Thread(threadStart, MaxStackSize.Value) { IsBackground = true }
                : new Thread(threadStart) { IsBackground = true };
        }

        /// <summary>
        /// De-schedule the current execution environment to sleep for some period.
        /// </summary>
        /// <param name="duration">Time to sleep.</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle to continue execution after the sleep period.</returns>
        public static Result Sleep(TimeSpan duration, Result result) {
            TaskTimerFactory.Current.New(duration, _ => result.Return(), null, TaskEnv.New());
            return result;
        }

        /// <summary>
        /// Blocks the thread for specified amount of time.
        /// </summary>
        /// <param name="timeout">Sleep time for thread.</param>
        public static void Sleep(TimeSpan timeout) {
            DispatchThreadEvictWorkItems();
            Thread.Sleep(timeout);
        }

        /// <summary>
        /// Wrap a <see cref="WaitHandle"/> with <see cref="Result{WaitHandle}"/> to allow Result style synchronization with the handle.
        /// </summary>
        /// <param name="handle">The handle to wrap</param>
        /// <param name="result">The <see cref="Result{WaitHandle}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle for the action's execution.</returns>
        public static Result<WaitHandle> WaitHandle(this WaitHandle handle, Result<WaitHandle> result) {
            ThreadPool.RegisterWaitForSingleObject(handle, (_unused, timedOut) => {
                if(timedOut) {
                    result.Throw(new TimeoutException());
                } else {
                    result.Return(handle);
                }
            }, null, (int)result.Timeout.TotalMilliseconds, true);
            return result;
        }

        /// <summary>
        /// Execute a system process.
        /// </summary>
        /// <param name="application">Application to execute.</param>
        /// <param name="cmdline">Command line parameters for he application.</param>
        /// <param name="input">Input stream to pipe into the application.</param>
        /// <param name="result">The Result instance to be returned by this method.</param>
        /// <returns>Synchronization handle for the process execution, providing the application's exit code, output Stream and error Stream</returns>
        public static Result<Tuplet<int, Stream, Stream>> ExecuteProcess(string application, string cmdline, Stream input, Result<Tuplet<int, Stream, Stream>> result) {
            Stream output = new MemoryStream();
            Stream error = new MemoryStream();
            Result<int> innerResult = new Result<int>(result.Timeout);
            Coroutine.Invoke(ExecuteProcess_Helper, application, cmdline, input, output, error, innerResult).WhenDone(_unused => {
                if(innerResult.HasException) {
                    result.Throw(innerResult.Exception);
                } else {

                    // reset stream positions
                    output.Position = 0;
                    error.Position = 0;

                    // return outcome
                    result.Return(new Tuplet<int, Stream, Stream>(innerResult.Value, output, error));
                }
            });
            return result;
        }

        /// <summary>
        /// Execute a system process.
        /// </summary>
        /// <param name="application">Application to execute.</param>
        /// <param name="cmdline">Command line parameters for he application.</param>
        /// <param name="input">Input stream to pipe into the application.</param>
        /// <param name="output"></param>
        /// <param name="error"></param>
        /// <param name="result">The <see cref="Result{Int32}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle for the process execution, providing the application's exit code</returns>
        public static Result<int> ExecuteProcess(string application, string cmdline, Stream input, Stream output, Stream error, Result<int> result) {
            return Coroutine.Invoke(ExecuteProcess_Helper, application, cmdline, input, output, error, result);
        }

        private static Yield ExecuteProcess_Helper(string application, string cmdline, Stream input, Stream output, Stream error, Result<int> result) {

            // start process
            var proc = new Process();
            proc.StartInfo.FileName = application;
            proc.StartInfo.Arguments = cmdline;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardInput = (input != null);
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.Start();

            // inject input
            if(input != null) {
                input.CopyToStream(proc.StandardInput.BaseStream, long.MaxValue, new Result<long>(TimeSpan.MaxValue)).WhenDone(_ => {

                    // trying closing the original input stream
                    try {
                        input.Close();
                    } catch { }

                    // try closing the process input pipe
                    try {
                        proc.StandardInput.Close();
                    } catch { }
                });
            }

            // extract output stream
            Result<long> outputDone = proc.StandardOutput.BaseStream.CopyToStream(output, long.MaxValue, new Result<long>(TimeSpan.MaxValue));

            // extract error stream
            Result<long> errorDone = proc.StandardError.BaseStream.CopyToStream(error, long.MaxValue, new Result<long>(TimeSpan.MaxValue));
            TaskTimer timer = TaskTimerFactory.Current.New(result.Timeout, t => {
                try {

                    // NOTE (steveb): we had to add the try..catch handler because mono throws an exception when killing a terminated process (why? who knows!)

                    proc.Kill();
                } catch { }
            }, null, TaskEnv.New());

            // wait for output and error streams to be done
            yield return new AResult[] { outputDone, errorDone }.Join();
            int? exitCode = WaitForExit(proc, result.Timeout);
            timer.Cancel();
            proc.Close();
            if(exitCode.HasValue) {
                result.Return(exitCode.Value);
            } else {
                result.Throw(new InvalidOperationException("Unable to access process exit code"));
            }
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <param name="begin">Lambda wrapping a no-arg async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle.</returns>
        public static Result From(Func<AsyncCallback, object, IAsyncResult> begin, Action<IAsyncResult> end, object state, Result result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        end(v);
                    } catch(Exception e) {
                        result.Throw(e);
                        return;
                    }
                    result.Return();
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T1">Type of asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a single argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">Asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle.</returns>
        public static Result From<T1>(Func<T1, AsyncCallback, object, IAsyncResult> begin, Action<IAsyncResult> end, T1 item1, object state, Result result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        end(v);
                    } catch(Exception e) {
                        result.Throw(e);
                        return;
                    }
                    result.Return();
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 2 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle.</returns>
        public static Result From<T1, T2>(Func<T1, T2, AsyncCallback, object, IAsyncResult> begin, Action<IAsyncResult> end, T1 item1, T2 item2, object state, Result result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        end(v);
                    } catch(Exception e) {
                        result.Throw(e);
                        return;
                    }
                    result.Return();
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <typeparam name="T3">Type of third asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 3 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="item3">Third asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle.</returns>
        public static Result From<T1, T2, T3>(Func<T1, T2, T3, AsyncCallback, object, IAsyncResult> begin, Action<IAsyncResult> end, T1 item1, T2 item2, T3 item3, object state, Result result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        end(v);
                    } catch(Exception e) {
                        result.Throw(e);
                        return;
                    }
                    result.Return();
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, item3, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <typeparam name="T3">Type of third asynchronous method argument.</typeparam>
        /// <typeparam name="T4">Type of fourth asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 4 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="item3">Third asynchronous method argument.</param>
        /// <param name="item4">Fourth asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle.</returns>
        public static Result From<T1, T2, T3, T4>(Func<T1, T2, T3, T4, AsyncCallback, object, IAsyncResult> begin, Action<IAsyncResult> end, T1 item1, T2 item2, T3 item3, T4 item4, object state, Result result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        end(v);
                    } catch(Exception e) {
                        result.Throw(e);
                        return;
                    }
                    result.Return();
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, item3, item4, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <typeparam name="T3">Type of third asynchronous method argument.</typeparam>
        /// <typeparam name="T4">Type of fourth asynchronous method argument.</typeparam>
        /// <typeparam name="T5">Type of fifth asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 5 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="item3">Third asynchronous method argument.</param>
        /// <param name="item4">Fourth asynchronous method argument.</param>
        /// <param name="item5">Fifth asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle.</returns>
        public static Result From<T1, T2, T3, T4, T5>(Func<T1, T2, T3, T4, T5, AsyncCallback, object, IAsyncResult> begin, Action<IAsyncResult> end, T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, object state, Result result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        end(v);
                    } catch(Exception e) {
                        result.Throw(e);
                        return;
                    }
                    result.Return();
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, item3, item4, item5, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <typeparam name="T3">Type of third asynchronous method argument.</typeparam>
        /// <typeparam name="T4">Type of fourth asynchronous method argument.</typeparam>
        /// <typeparam name="T5">Type of fifth asynchronous method argument.</typeparam>
        /// <typeparam name="T6">Type of sixth asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 6 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="item3">Third asynchronous method argument.</param>
        /// <param name="item4">Fourth asynchronous method argument.</param>
        /// <param name="item5">Fifth asynchronous method argument.</param>
        /// <param name="item6">Sixth asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle.</returns>
        public static Result From<T1, T2, T3, T4, T5, T6>(Func<T1, T2, T3, T4, T5, T6, AsyncCallback, object, IAsyncResult> begin, Action<IAsyncResult> end, T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, object state, Result result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        end(v);
                    } catch(Exception e) {
                        result.Throw(e);
                        return;
                    }
                    result.Return();
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, item3, item4, item5, item6, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T">Type of the asynchronous method return value.</typeparam>
        /// <param name="begin">Lambda wrapping a no argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle providing result value T.</returns>
        public static Result<T> From<T>(Func<AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, T> end, object state, Result<T> result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        result.Return(end(v));
                    } catch(Exception e) {
                        result.Throw(e);
                    }
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T">Type of the asynchronous method return value.</typeparam>
        /// <typeparam name="T1">Type of asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a single argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">Asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle providing result value T.</returns>
        public static Result<T> From<T, T1>(Func<T1, AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, T> end, T1 item1, object state, Result<T> result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        result.Return(end(v));
                    } catch(Exception e) {
                        result.Throw(e);
                    }
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T">Type of the asynchronous method return value.</typeparam>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 2 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle providing result value T.</returns>
        public static Result<T> From<T, T1, T2>(Func<T1, T2, AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, T> end, T1 item1, T2 item2, object state, Result<T> result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        result.Return(end(v));
                    } catch(Exception e) {
                        result.Throw(e);
                    }
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T">Type of the asynchronous method return value.</typeparam>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <typeparam name="T3">Type of third asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 3 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="item3">Third asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle providing result value T.</returns>
        public static Result<T> From<T, T1, T2, T3>(Func<T1, T2, T3, AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, T> end, T1 item1, T2 item2, T3 item3, object state, Result<T> result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        result.Return(end(v));
                    } catch(Exception e) {
                        result.Throw(e);
                    }
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, item3, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T">Type of the asynchronous method return value.</typeparam>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <typeparam name="T3">Type of third asynchronous method argument.</typeparam>
        /// <typeparam name="T4">Type of fourth asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 4 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="item3">Third asynchronous method argument.</param>
        /// <param name="item4">Fourth asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle providing result value T.</returns>
        public static Result<T> From<T, T1, T2, T3, T4>(Func<T1, T2, T3, T4, AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, T> end, T1 item1, T2 item2, T3 item3, T4 item4, object state, Result<T> result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        result.Return(end(v));
                    } catch(Exception e) {
                        result.Throw(e);
                    }
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, item3, item4, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T">Type of the asynchronous method return value.</typeparam>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <typeparam name="T3">Type of third asynchronous method argument.</typeparam>
        /// <typeparam name="T4">Type of fourth asynchronous method argument.</typeparam>
        /// <typeparam name="T5">Type of fifth asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 5 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="item3">Third asynchronous method argument.</param>
        /// <param name="item4">Fourth asynchronous method argument.</param>
        /// <param name="item5">Fifth asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle providing result value T.</returns>
        public static Result<T> From<T, T1, T2, T3, T4, T5>(Func<T1, T2, T3, T4, T5, AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, T> end, T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, object state, Result<T> result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        result.Return(end(v));
                    } catch(Exception e) {
                        result.Throw(e);
                    }
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, item3, item4, item5, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Convert an asynchronous call using the <see cref="AsyncCallback"/> pattern into one using a <see cref="Result"/> synchronization handle.
        /// </summary>
        /// <typeparam name="T">Type of the asynchronous method return value.</typeparam>
        /// <typeparam name="T1">Type of first asynchronous method argument.</typeparam>
        /// <typeparam name="T2">Type of second asynchronous method argument.</typeparam>
        /// <typeparam name="T3">Type of third asynchronous method argument.</typeparam>
        /// <typeparam name="T4">Type of fourth asynchronous method argument.</typeparam>
        /// <typeparam name="T5">Type of fifth asynchronous method argument.</typeparam>
        /// <typeparam name="T6">Type of sixth asynchronous method argument.</typeparam>
        /// <param name="begin">Lambda wrapping a 6 argument async call.</param>
        /// <param name="end">Action to execute on async completion.</param>
        /// <param name="item1">First asynchronous method argument.</param>
        /// <param name="item2">Second asynchronous method argument.</param>
        /// <param name="item3">Third asynchronous method argument.</param>
        /// <param name="item4">Fourth asynchronous method argument.</param>
        /// <param name="item5">Fifth asynchronous method argument.</param>
        /// <param name="item6">Sixth asynchronous method argument.</param>
        /// <param name="state">State object</param>
        /// <param name="result">The <see cref="Result{T}"/>instance to be returned by this method.</param>
        /// <returns>Synchronization handle providing result value T.</returns>
        public static Result<T> From<T, T1, T2, T3, T4, T5, T6>(Func<T1, T2, T3, T4, T5, T6, AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, T> end, T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, object state, Result<T> result) {

            // define continuation for asynchronous invocation
            var continuation = new Result<IAsyncResult>(TimeSpan.MaxValue).WhenDone(
                v => {
                    try {
                        result.Return(end(v));
                    } catch(Exception e) {
                        result.Throw(e);
                    }
                },
                result.Throw
            );

            //  begin asynchronous invocation
            try {
                begin(item1, item2, item3, item4, item5, item6, continuation.Return, state);
            } catch(Exception e) {
                result.Throw(e);
            }

            // return yield handle
            return result;
        }

        /// <summary>
        /// Waits for handle to be set.
        /// </summary>
        /// <param name="handle">Handle to wait on.</param>
        /// <param name="timeout">Timeout period. Use <cref see="TimeSpan"/>.MaxValue for no timeout.</param>
        /// <returns>Returns true if the handle was set before the timeout, false otherwise.</returns>
        public static bool WaitFor(WaitHandle handle, TimeSpan timeout) {
            DispatchThreadEvictWorkItems();
            return handle.WaitOne((timeout == TimeSpan.MaxValue) ? Timeout.Infinite : (int)timeout.TotalMilliseconds);
        }

        /// <summary>
        /// Waits for event to be signaled.
        /// </summary>
        /// <param name="monitor">Event to wait on.</param>
        /// <param name="timeout">Timeout period. Use <cref see="TimeSpan"/>.MaxValue for no timeout.</param>
        /// <returns>Returns true if the event was signaled before the timeout, false otherwise.</returns>
        public static bool WaitFor(MonitorSemaphore monitor, TimeSpan timeout) {
            DispatchThreadEvictWorkItems();
            return monitor.Wait(timeout);
        }

        private static int? WaitForExit(Process process, TimeSpan retryTime) {

            //NOTE (arnec): WaitForExit is unreliable on mono, so we have to loop on ExitCode to make sure
            //              the process really has exited

            DateTime end = GlobalClock.UtcNow.Add(retryTime);
            process.WaitForExit();
            do {
                try {
                    return process.ExitCode;
                } catch(InvalidOperationException) {
                    Sleep(TimeSpan.FromMilliseconds(50));
                }
            } while(end > GlobalClock.UtcNow);
            return null;
        }

        private static void DispatchThreadEvictWorkItems() {
            var thread = DispatchThread.CurrentThread;
            if(thread != null) {
                thread.EvictWorkItems();
            }
        }
    }
}
