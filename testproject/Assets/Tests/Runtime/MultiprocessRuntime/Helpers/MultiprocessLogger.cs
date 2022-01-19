using UnityEngine;
using System;
using System.Diagnostics;
using System.Net.Http;
using NUnit.Framework;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Collections.Generic;

namespace Unity.Netcode.MultiprocessRuntimeTests
{
    public class MultiprocessLogger
    {
        private static Logger s_Logger;

        static MultiprocessLogger() => s_Logger = new Logger(logHandler: new MultiprocessLogHandler());

        public static void Flush()
        {
            Thread.Sleep(1000);
            int canceledCount = 0;
            int totalCount = MultiprocessLogHandler.AllTasks.Count;
            int ranToCompletionCount = 0;
            int runningCount = 0;

            foreach (var task in MultiprocessLogHandler.AllTasks)
            {
                if (task.Status == TaskStatus.Canceled)
                {
                    canceledCount++;
                }
                else if (task.Status == TaskStatus.RanToCompletion)
                {
                    ranToCompletionCount++;
                }
                else if (task.Status == TaskStatus.Running)
                {
                    runningCount++;
                }
            }
            string msg = $"AllTasks.Count {totalCount} canceled: {canceledCount} completed: {ranToCompletionCount} running: {runningCount}";
            Console.WriteLine(msg);
            Log(msg);
            Thread.Sleep(1000);
        }

        public static void Log(string msg)
        {
            s_Logger.Log(msg);
        }

        public static void LogError(string msg)
        {
            s_Logger.LogError("ERROR", msg);
        }

        public static void LogWarning(string msg)
        {
            s_Logger.LogWarning("WARN", msg);
        }
    }

    public class MultiprocessLogHandler : ILogHandler
    {
        private static HttpClient s_HttpClient = new HttpClient();
        public static List<Task> AllTasks;
        public static long JobId;

        static MultiprocessLogHandler()
        {
            AllTasks = new List<Task>();
            string sJobId = Environment.GetEnvironmentVariable("YAMATO_JOB_ID");
            if (!long.TryParse(sJobId, out JobId))
            {
                JobId = -2;
            }
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            UnityEngine.Debug.unityLogger.LogException(exception, context);
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string testName = null;
            try
            {
                testName = TestContext.CurrentContext.Test.Name;
            }
            catch (Exception)
            {
                // ignored
            }

            if (string.IsNullOrEmpty(testName))
            {
                testName = "unknown";
            }

            var st = new StackTrace(true);
            string method1 = st.GetFrame(1).GetMethod().Name;
            string method2 = "2";
            string method3 = "3";
            if (st.FrameCount > 3)
            {
                method2 = st.GetFrame(2).GetMethod().Name;
                method3 = st.GetFrame(3).GetMethod().Name;
            }
            UnityEngine.Debug.LogFormat(logType, LogOption.NoStacktrace, context, $"MPLOG ({DateTime.Now:T}) : {method3} : {method2} : {method1} : {testName} : {format}", args);
            var webLog = new WebLog();
            webLog.Message = $"{DateTime.Now:T} {args[0].ToString()}";
            webLog.ReferenceId = JobId;
            webLog.TestMethod = testName;
            webLog.ClientEventDate = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            string json = JsonUtility.ToJson(webLog);
            var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Task t = PostBasicAsync(webLog, cancelAfterDelay.Token);
            AllTasks.Add(t);
        }

        private static async Task PostBasicAsync(WebLog content, CancellationToken cancellationToken)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://multiprocess-log-event-manager.test.ds.unity3d.com/api/MultiprocessLogEvent");
            var json = JsonUtility.ToJson(content);
            using var stringContent = new StringContent(json, Encoding.UTF8, "application/json");
            request.Content = stringContent;

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            // response.EnsureSuccessStatusCode();
        }
    }

    [Serializable]
    public struct WebLog
    {
        public string Message;
        public long ReferenceId;
        public string TestMethod;
        public string ClientEventDate;

        public override string ToString()
        {
            return base.ToString() + " " + Message;
        }
    }
}
