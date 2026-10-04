using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ViewFX.Editor
{
    [InitializeOnLoad]
    internal static class DownloadService
    {
        [Serializable] private class Connection { public string siteUrl; public string token; }
        [Serializable] private class Download { public string id, status, packageUrl, effectName; }
        [Serializable] private class Reply { public Download download; public string status; }
        [Serializable] private class Heartbeat { public string sessionId, projectName, unityVersion, downloadId; public bool busy; }
        [Serializable] private class Update { public string sessionId, status, error; public float progress; }
        private static Connection connection;
        private static string sessionId, filePath;
        private static Download job;
        private static UnityWebRequest control, transfer;
        private static Action<Reply> callback;
        private static Update pendingUpdate;
        private static double nextCheck, nextRequest;
        private static bool sendHeartbeat = true, awaitingImport;
        internal static string Status { get; private set; } = "";

        static DownloadService()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += StopRequests;
            EditorApplication.quitting += StopRequests;
        }

        private static bool Busy => EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode;
        private static string PendingKey => "ViewFX.Download." + CredentialStore.ProjectKey;

        private static void StopRequests()
        {
            control?.Abort(); control?.Dispose(); control = null;
            transfer?.Abort(); transfer?.Dispose(); transfer = null;
        }

        private static void Tick()
        {
            try { TickSafe(); }
            catch (Exception error)
            {
                Debug.LogException(error);
                Status = "ViewFX download error: " + error.Message;
                if (job != null) pendingUpdate = new Update { sessionId = sessionId, status = "failed", progress = 0, error = "Unity could not download or open the effect package." };
                nextRequest = EditorApplication.timeSinceStartup + 3;
            }
        }

        private static void TickSafe()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now >= nextCheck)
            {
                nextCheck = now + 2;
                string saved = CredentialStore.Load();
                var latest = string.IsNullOrEmpty(saved) ? null : JsonUtility.FromJson<Connection>(saved);
                if (latest?.token != connection?.token || latest?.siteUrl != connection?.siteUrl)
                {
                    StopRequests();
                    if (job != null) Discard();
                    connection = latest;
                    if (!string.IsNullOrEmpty(connection?.token))
                    {
                        string key;
                        using (var sha = SHA256.Create()) key = "ViewFX.Session." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(connection.token))).Replace("-", "");
                        sessionId = SessionState.GetString(key, "");
                        if (string.IsNullOrEmpty(sessionId)) { sessionId = Guid.NewGuid().ToString(); SessionState.SetString(key, sessionId); }
                        string pending = SessionState.GetString(PendingKey, "");
                        if (!string.IsNullOrEmpty(pending)) job = JsonUtility.FromJson<Download>(pending);
                        if (job != null && string.IsNullOrEmpty(job.id)) Discard();
                    }
                }
            }
            if (string.IsNullOrEmpty(connection?.token)) return;
            if (control != null && control.isDone)
            {
                var completed = control;
                var handler = callback;
                control = null; callback = null;
                try
                {
                    if (completed.result == UnityWebRequest.Result.Success)
                    {
                        var reply = JsonUtility.FromJson<Reply>(completed.downloadHandler.text);
                        // Unity can deserialize a JSON null into an empty serializable object.
                        // Only a download with an ID represents a real server job.
                        if (reply.download != null && string.IsNullOrEmpty(reply.download.id)) reply.download = null;
                        handler(reply);
                    }
                    else if (completed.responseCode == 409) Discard();
                    else if (completed.responseCode == 401) { StopRequests(); Discard(); CredentialStore.Clear(); connection = null; }
                    else { Status = "Waiting for the ViewFX connection..."; nextRequest = now + 3; }
                }
                finally { completed.Dispose(); }
            }
            if (connection == null) return;
            if (transfer != null)
            {
                if (transfer.downloadedBytes > 70UL * 1024 * 1024)
                {
                    transfer.Abort(); transfer.Dispose(); transfer = null;
                    pendingUpdate = new Update { sessionId = sessionId, status = "failed", progress = 0, error = "The effect package exceeds the 70 MiB download limit." };
                }
            }
            if (transfer != null)
            {
                Status = "Downloading " + job.effectName + "... " + Mathf.RoundToInt(transfer.downloadProgress * 100) + "%";
                if (transfer.isDone)
                {
                    bool success = transfer.result == UnityWebRequest.Result.Success;
                    string failure = success ? null : "Package download failed (HTTP " + transfer.responseCode + "): " + transfer.error;
                    transfer.Dispose(); transfer = null;
                    if (success)
                    {
                        using (var stream = File.OpenRead(filePath)) success = stream.ReadByte() == 0x1f && stream.ReadByte() == 0x8b;
                        if (!success) failure = "The downloaded file is not a valid Unity package.";
                    }
                    pendingUpdate = new Update { sessionId = sessionId, status = success ? "completed" : "failed", progress = success ? 1 : 0,
                        error = failure == null ? null : failure.Substring(0, Math.Min(failure.Length, 300)) };
                }
            }
            if (awaitingImport && !Busy)
            {
                awaitingImport = false;
                string path = filePath;
                // The user reviews all imported assets in Unity's standard import dialog.
                AssetDatabase.ImportPackage(path, true);
                Status = "Download complete. Review the package in Unity's import dialog.";
                job = null; pendingUpdate = null;
                SessionState.EraseString(PendingKey);
            }
            if (control != null || now < nextRequest) return;
            nextRequest = now + 1;
            if (!sendHeartbeat && job != null && (pendingUpdate != null || transfer != null))
            {
                var update = pendingUpdate ?? new Update { sessionId = sessionId, status = "downloading", progress = Mathf.Clamp01(transfer.downloadProgress) };
                Send("/api/unity/editor/downloads/" + job.id, JsonUtility.ToJson(update), reply =>
                {
                    if (update.status == "completed")
                    {
                        awaitingImport = true;
                        job.status = "completed";
                        SessionState.SetString(PendingKey, JsonUtility.ToJson(job));
                        pendingUpdate = null;
                    }
                    else if (update.status == "failed") { Status = update.error; Discard(); }
                });
                sendHeartbeat = true;
                return;
            }
            Send("/api/unity/editor/heartbeat", JsonUtility.ToJson(new Heartbeat {
                sessionId = sessionId, projectName = new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name,
                unityVersion = Application.unityVersion, busy = Busy, downloadId = job?.id
            }), reply =>
            {
                if (job != null && (reply.download == null || reply.download.status == "cancelled" || reply.download.status == "failed"))
                { Status = "Download cancelled or no longer available."; Discard(); return; }
                if (reply.download == null) return;
                job = reply.download;
                SessionState.SetString(PendingKey, JsonUtility.ToJson(job));
                filePath = Path.Combine(Path.GetTempPath(), "ViewFX", sessionId, job.id + ".unitypackage");
                if (job.status == "completed")
                {
                    if (File.Exists(filePath)) awaitingImport = true;
                    else { job = null; SessionState.EraseString(PendingKey); }
                    return;
                }
                if (transfer == null && pendingUpdate == null && !Busy) StartDownload();
            });
            sendHeartbeat = false;
        }

        private static void StartDownload()
        {
            bool authenticated = job.packageUrl == "/api/unity/editor/downloads/" + job.id + "/package";
            if (!authenticated && (!Uri.TryCreate(job.packageUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                (uri.Host != "github.com" && uri.Host != "raw.githubusercontent.com") || !string.IsNullOrEmpty(uri.UserInfo))
                )
                throw new InvalidOperationException("Invalid effect package URL: " + (job.packageUrl ?? "<missing>") + ".");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            transfer = new UnityWebRequest(authenticated ? connection.siteUrl + job.packageUrl : job.packageUrl, "GET") {
                downloadHandler = new DownloadHandlerFile(filePath) { removeFileOnAbort = true }, timeout = 600,
                redirectLimit = authenticated ? 0 : 32
            };
            if (authenticated) transfer.SetRequestHeader("Authorization", "Bearer " + connection.token);
            transfer.SendWebRequest();
        }

        private static void Discard()
        {
            transfer?.Abort(); transfer?.Dispose(); transfer = null;
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) File.Delete(filePath);
            job = null; pendingUpdate = null; awaitingImport = false; filePath = null;
            SessionState.EraseString(PendingKey);
        }

        private static void Send(string path, string body, Action<Reply> handler)
        {
            control = new UnityWebRequest(connection.siteUrl + path, "POST") { downloadHandler = new DownloadHandlerBuffer(),
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)), timeout = 10, redirectLimit = 0 };
            control.SetRequestHeader("Content-Type", "application/json");
            control.SetRequestHeader("Authorization", "Bearer " + connection.token);
            callback = handler;
            control.SendWebRequest();
        }
    }
}
