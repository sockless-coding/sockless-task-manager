using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>
/// Checks Authenticode signatures with WinVerifyTrust: the file's own signature, else a Windows
/// catalog that lists its hash (how most of Windows' own files are signed). Shared by Autostart and
/// Drivers. No revocation checks
/// and no downloads, so it never goes online. One instance per scan thread; not thread-safe.
/// </summary>
internal sealed unsafe partial class SignatureChecker : IDisposable
{
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint ChoiceCatalog = 2;
    private const uint StateVerify = 1;
    private const uint StateClose = 2;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const uint SimpleDisplayName = 4;

    private const int NoSignature = unchecked((int)0x800B0100);
    private const int SubjectFormUnknown = unchecked((int)0x800B0003);
    private const int ProviderUnknown = unchecked((int)0x800B0001);
    private const int Expired = unchecked((int)0x800B0101);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private nint _sha256;
    private nint _sha1;
    private bool _triedSha256;
    private bool _triedSha1;

    public Signature Check(string path)
    {
        SafeFileHandle file;
        try
        {
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Signature(SignatureState.Unreadable);
        }

        using (file)
        {
            var embedded = VerifyFile(path, file.DangerousGetHandle());
            if (embedded is not null)
                return embedded;

            return VerifyCatalog(path, file.DangerousGetHandle(), "SHA256", ref _sha256, ref _triedSha256)
                ?? VerifyCatalog(path, file.DangerousGetHandle(), "SHA1", ref _sha1, ref _triedSha1)
                ?? new Signature(SignatureState.Unsigned);
        }
    }

    /// <summary>The file's own signature; null when it has none.</summary>
    private static Signature? VerifyFile(string path, nint handle)
    {
        fixed (char* p = path)
        {
            var info = new WintrustFileInfo { Size = (uint)sizeof(WintrustFileInfo), Path = p, File = handle };
            var data = new WintrustData
            {
                Size = (uint)sizeof(WintrustData),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                Info = &info,
                StateAction = StateVerify,
                ProviderFlags = CacheOnlyUrlRetrieval,
            };

            var result = Verify(&data);
            return result is NoSignature or SubjectFormUnknown or ProviderUnknown ? null : Outcome(result, &data, byCatalog: false);
        }
    }

    /// <summary>A catalog that lists the file's hash; null when none does.</summary>
    private static Signature? VerifyCatalog(string path, nint handle, string algorithm, ref nint admin, ref bool tried)
    {
        if (!tried)
        {
            tried = true;
            nint context;
            fixed (char* name = algorithm)
            {
                if (CryptCATAdminAcquireContext2(&context, null, name, 0, 0))
                    admin = context;
            }
        }

        if (admin == 0)
            return null;

        uint size = 64;
        var hash = stackalloc byte[64];
        if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, &size, hash, 0))
            return null;

        nint previous = 0;
        var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, &previous);
        if (catalog == 0)
            return null;

        try
        {
            var info = new CatalogInfo { Size = (uint)sizeof(CatalogInfo) };
            if (!CryptCATCatalogInfoFromContext(catalog, &info, 0))
                return null;

            var tag = Convert.ToHexString(new ReadOnlySpan<byte>(hash, (int)size));
            fixed (char* member = path)
            fixed (char* tagText = tag)
            {
                var catalogInfo = new WintrustCatalogInfo
                {
                    Size = (uint)sizeof(WintrustCatalogInfo),
                    CatalogPath = info.File,
                    MemberTag = tagText,
                    MemberPath = member,
                    MemberFile = handle,
                    Hash = hash,
                    HashSize = size,
                    CatalogAdmin = admin,
                };
                var data = new WintrustData
                {
                    Size = (uint)sizeof(WintrustData),
                    UiChoice = UiNone,
                    RevocationChecks = RevokeNone,
                    UnionChoice = ChoiceCatalog,
                    Info = &catalogInfo,
                    StateAction = StateVerify,
                    ProviderFlags = CacheOnlyUrlRetrieval,
                };

                return Outcome(Verify(&data), &data, byCatalog: true);
            }
        }
        finally
        {
            CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
        }
    }

    private static int Verify(WintrustData* data)
    {
        var action = GenericVerifyV2;
        return WinVerifyTrust(-1, &action, data);
    }

    /// <summary>Reads the signer while the verification state is open, then closes it.</summary>
    private static Signature Outcome(int result, WintrustData* data, bool byCatalog)
    {
        try
        {
            var signer = Signer(data->State);
            return result switch
            {
                0 => new Signature(SignatureState.Signed, signer, byCatalog),
                Expired => new Signature(SignatureState.Signed, signer is null ? "Certificate expired" : $"{signer} (certificate expired)", byCatalog),
                _ => new Signature(SignatureState.Invalid, signer, byCatalog),
            };
        }
        finally
        {
            data->StateAction = StateClose;
            var action = GenericVerifyV2;
            WinVerifyTrust(-1, &action, data);
        }
    }

    private static string? Signer(nint state)
    {
        if (state == 0)
            return null;

        var provider = WTHelperProvDataFromStateData(state);
        if (provider == 0)
            return null;

        var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        if (signer == 0)
            return null;

        var cert = WTHelperGetProvCertFromChain(signer, 0);
        if (cert == 0)
            return null;

        // CRYPT_PROVIDER_CERT: cbStruct, then the certificate context.
        var context = *(nint*)((byte*)cert + sizeof(nint));
        if (context == 0)
            return null;

        var name = stackalloc char[256];
        var length = CertGetNameStringW(context, SimpleDisplayName, 0, 0, name, 256);
        return length > 1 ? new string(name, 0, (int)length - 1) : null;
    }

    public void Dispose()
    {
        if (_sha256 != 0)
            CryptCATAdminReleaseContext(_sha256, 0);
        if (_sha1 != 0)
            CryptCATAdminReleaseContext(_sha1, 0);
        _sha256 = _sha1 = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint Size;
        public char* Path;
        public nint File;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustCatalogInfo
    {
        public uint Size;
        public uint CatalogVersion;
        public char* CatalogPath;
        public char* MemberTag;
        public char* MemberPath;
        public nint MemberFile;
        public byte* Hash;
        public uint HashSize;
        public nint CatalogContext;
        public nint CatalogAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public void* Info;
        public uint StateAction;
        public nint State;
        public char* UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CatalogInfo
    {
        public uint Size;
        public fixed char File[260];
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, Guid* action, WintrustData* data);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(nint* admin, Guid* subsystem, char* hashAlgorithm, nint strongHashPolicy, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminCalcHashFromFileHandle2(nint admin, nint file, uint* hashSize, byte* hash, uint flags);

    [LibraryImport("wintrust.dll")]
    private static partial nint CryptCATAdminEnumCatalogFromHash(nint admin, byte* hash, uint hashSize, uint flags, nint* previous);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATCatalogInfoFromContext(nint catalog, CatalogInfo* info, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseCatalogContext(nint admin, nint catalog, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(nint admin, uint flags);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint state);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(nint provider, uint signer, int counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvCertFromChain(nint signer, uint index);

    [LibraryImport("crypt32.dll")]
    private static partial uint CertGetNameStringW(nint context, uint type, uint flags, nint typePara, char* name, uint size);
}

internal enum SignatureState
{
    /// <summary>Not verified yet: the list shows before every file is checked.</summary>
    Pending,
    Signed,
    Unsigned,

    /// <summary>A signature that doesn't verify: a changed file, a distrusted or revoked certificate.</summary>
    Invalid,

    /// <summary>No file to check.</summary>
    None,

    /// <summary>The file is there but can't be opened to check (in use exclusively, or denied).</summary>
    Unreadable,
}

/// <param name="Signer">The certificate's subject, when signed.</param>
/// <param name="ByCatalog">Signed through a Windows catalog rather than in the file itself.</param>
internal sealed record Signature(SignatureState State, string? Signer = null, bool ByCatalog = false)
{
    /// <summary>Signed as part of an app package (Autostart: a packaged app's files are signed by its package).</summary>
    public bool ByPackage { get; init; }

    public static readonly Signature Pending = new(SignatureState.Pending);
    public static readonly Signature None = new(SignatureState.None);
}
