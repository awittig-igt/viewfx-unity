using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ViewFX.Editor
{
    public sealed class ViewFXWindow : EditorWindow
    {
        [Serializable] private class Account { public string name; public string email; }
        [Serializable] private class State
        {
            public string siteUrl = "http://localhost:5173";
            public string id, pollToken, approvalUrl, expiresAt, token;
            public Account user;
        }
        [Serializable] private class Reply
        {
            public string id, pollToken, approvalUrl, expiresAt, status, token, error;
            public Account user;
        }
        [Serializable] private class ProjectRequest { public string projectName; public string unityVersion; }

        private State state = new State();
        private UnityWebRequest activeRequest;
        private Action<Reply> onReply;
        private double nextPoll;
        private string message = "Connect your ViewFX account.";
        private string StateKey => "ViewFX.Pending." + CredentialStore.ProjectKey;

        [InitializeOnLoadMethod]
        private static void PromptOnInstall()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode) return;
                string key = "ViewFX.Prompted." + CredentialStore.ProjectKey;
                if (EditorPrefs.GetBool(key)) return;
                EditorPrefs.SetBool(key, true);
                Open();
            };
        }

        [MenuItem("Window/ViewFX")]
        public static void Open() => GetWindow<ViewFXWindow>("ViewFX");

        private void OnEnable()
        {
            minSize = new Vector2(340, 270);
            try
            {
                string saved = CredentialStore.Load();
                if (string.IsNullOrEmpty(saved)) saved = SessionState.GetString(StateKey, "");
                if (!string.IsNullOrEmpty(saved)) state = JsonUtility.FromJson<State>(saved);
                if (!string.IsNullOrEmpty(state.token)) Verify();
            }
            catch (Exception error) { message = error.Message; }
            EditorApplication.update += Tick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            activeRequest?.Abort();
            activeRequest?.Dispose();
            activeRequest = null;
        }

        private void RememberPending() => SessionState.SetString(StateKey, JsonUtility.ToJson(state));

        private void OnGUI()
        {
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("ViewFX", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Project", new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name);
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(message, MessageType.Info);
            bool connected = !string.IsNullOrEmpty(state.token);
            bool pending = !string.IsNullOrEmpty(state.id);
            using (new EditorGUI.DisabledScope(connected || pending || activeRequest != null))
                state.siteUrl = EditorGUILayout.TextField("ViewFX site", state.siteUrl);
            if (connected)
            {
                EditorGUILayout.LabelField("Account", state.user?.name ?? "");
                EditorGUILayout.LabelField("Email", state.user?.email ?? "");
#if !UNITY_EDITOR_WIN
                EditorGUILayout.HelpBox("This connection lasts until Unity closes on this platform.", MessageType.Info);
#endif
                using (new EditorGUI.DisabledScope(activeRequest != null))
                {
                    if (GUILayout.Button("Disconnect")) Disconnect();
                    if (GUILayout.Button("Check Connection")) Verify();
                }
            }
            else if (pending)
            {
                if (GUILayout.Button("Open Browser Again")) Application.OpenURL(state.approvalUrl);
                using (new EditorGUI.DisabledScope(activeRequest != null))
                    if (GUILayout.Button("Cancel")) Cancel();
            }
            else
            {
                using (new EditorGUI.DisabledScope(activeRequest != null))
                    if (GUILayout.Button("Connect to ViewFX")) Connect();
            }
        }

        private void Connect()
        {
            if (!Uri.TryCreate(state.siteUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                message = "Enter an HTTPS site address, or HTTP localhost for development.";
                return;
            }
            state.siteUrl = uri.GetLeftPart(UriPartial.Authority);
            message = "Starting connection...";
            Send("POST", "/api/unity/requests", "", JsonUtility.ToJson(new ProjectRequest {
                projectName = new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name,
                unityVersion = Application.unityVersion
            }), reply =>
            {
                if (!Uri.TryCreate(reply.approvalUrl, UriKind.Absolute, out var approval) || approval.GetLeftPart(UriPartial.Authority) != state.siteUrl)
                { message = "The server returned a different site address. Check ViewFX site configuration."; return; }
                state.id = reply.id;
                state.pollToken = reply.pollToken;
                state.approvalUrl = reply.approvalUrl;
                state.expiresAt = reply.expiresAt;
                RememberPending();
                message = "Waiting for Allow or Deny in your browser...";
                Application.OpenURL(state.approvalUrl);
            });
        }

        private void Tick()
        {
            if (activeRequest != null)
            {
                if (!activeRequest.isDone) return;
                var completed = activeRequest;
                var callback = onReply;
                activeRequest = null;
                onReply = null;
                try
                {
                    Reply reply = null;
                    try { reply = JsonUtility.FromJson<Reply>(completed.downloadHandler.text); } catch (ArgumentException) { }
                    if (completed.result != UnityWebRequest.Result.Success)
                    {
                        if (completed.responseCode == 401) Clear();
                        message = reply?.error ?? "Could not reach ViewFX. Check your connection and try again.";
                    }
                    else if (reply != null) callback(reply);
                    else message = "ViewFX returned an invalid response. Try again.";
                }
                catch (Exception error) { message = "Could not complete connection: " + error.Message; }
                finally { completed.Dispose(); Repaint(); }
            }
            if (activeRequest != null || string.IsNullOrEmpty(state.id) || EditorApplication.timeSinceStartup < nextPoll) return;
            if (DateTimeOffset.TryParse(state.expiresAt, out var expiry) && expiry <= DateTimeOffset.UtcNow)
            { Clear(); message = "Connection request expired. Connect again to retry."; Repaint(); return; }
            nextPoll = EditorApplication.timeSinceStartup + 3;
            Send("POST", "/api/unity/requests/" + state.id + "/poll", state.pollToken, "{}", reply =>
            {
                if (reply.status == "connected")
                {
                    // Persist before discarding the pending request, so storage failure can be retried.
                    var connected = new State { siteUrl = state.siteUrl, token = reply.token, user = reply.user };
                    CredentialStore.Save(JsonUtility.ToJson(connected));
                    state = connected;
                    SessionState.EraseString(StateKey);
                    message = "Connected to ViewFX.";
                }
                else if (reply.status != "pending")
                {
                    Clear();
                    message = reply.status == "denied" ? "Connection denied. You can connect again." : "Connection request expired or cancelled. Connect again to retry.";
                }
            });
        }

        private void Verify() => Send("GET", "/api/unity/connection", state.token, null, reply =>
        { state.user = reply.user; message = "Connected to ViewFX."; });

        private void Disconnect()
        {
            message = "Disconnecting...";
            Send("DELETE", "/api/unity/connection", state.token, null, _ =>
            { Clear(); message = "Disconnected from ViewFX."; });
        }

        private void Cancel() => Send("DELETE", "/api/unity/requests/" + state.id, state.pollToken, null, _ =>
        { Clear(); message = "Connection cancelled."; });

        private void Clear()
        {
            CredentialStore.Clear();
            SessionState.EraseString(StateKey);
            state = new State { siteUrl = state.siteUrl };
        }

        private void Send(string method, string path, string token, string body, Action<Reply> callback)
        {
            activeRequest = new UnityWebRequest(state.siteUrl + path, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 15, redirectLimit = 0 };
            if (body != null)
            {
                activeRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                activeRequest.SetRequestHeader("Content-Type", "application/json");
            }
            if (!string.IsNullOrEmpty(token)) activeRequest.SetRequestHeader("Authorization", "Bearer " + token);
            onReply = callback;
            activeRequest.SendWebRequest();
        }
    }
}
