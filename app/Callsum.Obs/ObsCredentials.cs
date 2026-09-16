using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Callsum.Obs;

/// <summary>Пароль WebSocket в Диспетчере учётных данных Windows.</summary>
public static class ObsCredentials
{
    /// <summary>Имя записи видно в разделе «Учётные данные Windows».</summary>
    public const string TargetName = "callsum/obs-websocket";

    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxBlobBytes = 512;

    /// <summary>Прочитать сохранённый пароль. Пусто — записи ещё нет.</summary>
    public static string Read()
    {
        if (!CredRead(TargetName, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return "";
            }

            throw new ObsCredentialsException(
                "Не удалось прочитать пароль OBS из Диспетчера учётных данных Windows",
                new Win32Exception(error));
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return "";
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>Сохранить пароль. Пустое поле удаляет прежнюю запись.</summary>
    public static void Save(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            Delete();
            return;
        }

        var blob = Encoding.Unicode.GetBytes(password);
        if (blob.Length > MaxBlobBytes)
        {
            throw new ObsCredentialsException("Пароль OBS слишком длинный для Диспетчера учётных данных Windows");
        }

        var target = Marshal.StringToCoTaskMemUni(TargetName);
        var secret = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, secret, blob.Length);
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = secret,
                Persist = LocalMachinePersistence,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new ObsCredentialsException(
                    "Не удалось сохранить пароль OBS в Диспетчере учётных данных Windows",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(secret);
            Marshal.FreeCoTaskMem(target);
        }
    }

    private static void Delete()
    {
        if (!CredDelete(TargetName, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new ObsCredentialsException(
                    "Не удалось удалить пароль OBS из Диспетчера учётных данных Windows",
                    new Win32Exception(error));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>Диспетчер учётных данных не дал прочитать или сохранить секрет.</summary>
public sealed class ObsCredentialsException : Exception
{
    public ObsCredentialsException(string message, Exception? inner = null) : base(message, inner) { }
}
