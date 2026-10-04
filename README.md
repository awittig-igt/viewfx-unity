# ViewFX for Unity

Editor-only companion package. Requires Unity 2022.3 or newer.

## Install from Git

In Unity's Package Manager, choose **Install package from Git URL** (or **Add package from git URL** on older editors) and paste:

```text
https://github.com/awittig-igt/viewfx-unity.git
```

## Install locally

In Unity's Package Manager, choose **Install package from disk** (or **Add package from disk** on older editors) and select this folder's `package.json`. ViewFX opens on first installation. Reopen it through **Window > ViewFX**.

## Connect

Start the ViewFX API and website. Set **ViewFX site** to `http://localhost:5173` for local development, or the deployed HTTPS origin. Click **Connect to ViewFX**, sign in on the website, and choose **Allow** or **Deny**. Requests expire after ten minutes. **Cancel** invalidates a pending request. **Disconnect** revokes the connection on the server before clearing the local credential; when offline, retry disconnect once the server is reachable.

Connections are independent per project path and operating-system user. On Windows, credentials are encrypted with Windows DPAPI and saved under the user's local application data, outside the Unity project. On macOS/Linux this first version uses Unity SessionState, so reconnect after closing Unity. Credentials expire after 90 days. **Check Connection** verifies the current credential.

No effect downloads or uploads are implemented in this release.
