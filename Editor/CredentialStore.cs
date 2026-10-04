using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ViewFX.Editor
{
    internal static class CredentialStore
    {
        internal static string ProjectKey
        {
            get
            {
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(Application.dataPath)))).Replace("-", "");
            }
        }

        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViewFX", ProjectKey + ".credential");
        private static string SessionKey => "ViewFX.Credential." + ProjectKey;

        internal static string Load()
        {
#if UNITY_EDITOR_WIN
            return File.Exists(FilePath) ? Encoding.UTF8.GetString(Transform(File.ReadAllBytes(FilePath), false)) : "";
#else
            return SessionState.GetString(SessionKey, "");
#endif
        }

        internal static void Save(string value)
        {
#if UNITY_EDITOR_WIN
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllBytes(FilePath, Transform(Encoding.UTF8.GetBytes(value), true));
#else
            SessionState.SetString(SessionKey, value);
#endif
        }

        internal static void Clear()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            SessionState.EraseString(SessionKey);
        }

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static byte[] Transform(byte[] bytes, bool protect)
        {
            var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length);
                bool success = protect
                    ? CryptProtectData(ref input, "ViewFX", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!success) throw new InvalidOperationException("Windows could not access the ViewFX credential.");
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
#endif
    }
}
