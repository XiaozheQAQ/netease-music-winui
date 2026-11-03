// Validation deliberately uses an in-memory certificate trust anchor. It never
// imports a certificate or changes the Windows trust stores.
#nullable enable
#pragma warning disable SYSLIB0057
using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LumaMusic.Packaging
{
    public sealed class MsixVerificationResult
    {
        public string PackagePath { get; set; } = "";
        public string Identity { get; set; } = "";
        public string Version { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string CertificateThumbprint { get; set; } = "";
        public bool IsSelfSigned { get; set; }
        public int PayloadFiles { get; set; }
        public long PayloadBytes { get; set; }
        public string SignatureStatus { get; set; } = "Verified";
        public string ContentIntegrity { get; set; } = "Verified against the cryptographically signed block map";
        public string ArchiveMetadata { get; set; } = "Managed verification uses the signed block map, not recomputed AXPC/AXCD; the native Windows SIP verification result is reported separately";
        public string CertificateValidation { get; set; } = "Not requested";
        public string WindowsTrustError { get; set; } = "Not checked";
        public string WindowsTrust { get; set; } = "Not modified or assumed by this verifier";
        public string[] VerifiedChecks { get; set; } = Array.Empty<string>();
    }

    public static class MsixVerifier
    {
        private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
        private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
        private const string IndirectDataOid = "1.3.6.1.4.1.311.2.1.4";
        private const string BlockMapNamespace = "http://schemas.microsoft.com/appx/2010/blockmap";
        private const string ManifestNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

        public static MsixVerificationResult Verify(string packagePath, string certificatePath)
        {
            if (string.IsNullOrWhiteSpace(certificatePath))
                throw new ArgumentException("A public signing certificate is required.", nameof(certificatePath));
            return VerifyCore(packagePath, Path.GetFullPath(certificatePath));
        }

        // This entry point allows independent content checks of SDK fixtures.
        // The release script always calls Verify with its expected public CER.
        public static MsixVerificationResult VerifyContent(string packagePath) => VerifyCore(packagePath, null);

        public static string GetWindowsTrustError(string packagePath)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows signature policy verification requires Windows.");
            packagePath = Path.GetFullPath(packagePath);
            if (!File.Exists(packagePath)) throw new FileNotFoundException("The package was not found.", packagePath);
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // GENERIC_VERIFY_V2
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = packagePath
            };
            var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = filePointer,
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProviderFlags = 0x1000 | 0x10, // cache-only retrieval, no revocation network requests
                UiContext = 1 // installation policy context; no UI is permitted
            };
            bool stateRequested = false;
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            try
            {
                int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                stateRequested = true;
                return "0x" + unchecked((uint)status).ToString("X8", CultureInfo.InvariantCulture);
            }
            finally
            {
                if (stateRequested)
                {
                    data.StateAction = 2; // WTD_STATEACTION_CLOSE, including failure results
                    WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                }
                Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
                Marshal.FreeHGlobal(filePointer);
            }
        }

        private static MsixVerificationResult VerifyCore(string packagePath, string? certificatePath)
        {
            packagePath = Path.GetFullPath(packagePath);
            using var archive = ZipFile.OpenRead(packagePath);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                var name = NormalizePath(Uri.UnescapeDataString(entry.FullName));
                if (!entries.TryAdd(name, entry))
                    throw new InvalidDataException("Duplicate decoded package path: " + name);
            }

            var signature = ReadBytes(RequireEntry(entries, "AppxSignature.p7x"), 16 * 1024 * 1024);
            if (signature.Length < 5 || Encoding.ASCII.GetString(signature, 0, 4) != "PKCX")
                throw new InvalidDataException("The MSIX signature has no valid PKCX header.");
            var cms = new SignedCms();
            cms.Decode(signature.AsSpan(4).ToArray());
            if (cms.ContentInfo.ContentType.Value != IndirectDataOid || cms.SignerInfos.Count != 1)
                throw new InvalidDataException("Expected one Appx indirect-data signer.");
            if (cms.SignerInfos[0].DigestAlgorithm.Value != Sha256Oid)
                throw new InvalidDataException("The CMS signature digest algorithm is not SHA-256.");
            cms.CheckSignature(verifySignatureOnly: true);
            var signer = cms.SignerInfos[0].Certificate
                ?? throw new InvalidDataException("The CMS signer certificate is missing.");
            CheckCertificatePurposeAndDates(signer);

            var digests = ReadSignedDigestTable(cms.ContentInfo.Content);
            foreach (var requiredTag in new[] { "AXPC", "AXCD", "AXCT", "AXBM" })
                if (!digests.ContainsKey(requiredTag))
                    throw new InvalidDataException("The signed digest table is missing " + requiredTag + ".");
            var blockMap = ReadBytes(RequireEntry(entries, "AppxBlockMap.xml"), 64 * 1024 * 1024);
            CheckDigest(digests, "AXBM", blockMap);
            CheckDigest(digests, "AXCT", ReadBytes(RequireEntry(entries, "[Content_Types].xml"), 4 * 1024 * 1024));
            bool hasCodeIntegrity = entries.TryGetValue("AppxMetadata/CodeIntegrity.cat", out var codeIntegrity);
            if (hasCodeIntegrity != digests.ContainsKey("AXCI"))
                throw new InvalidDataException("The code-integrity catalog and signed AXCI digest disagree.");
            if (codeIntegrity is not null)
                CheckDigest(digests, "AXCI", ReadBytes(codeIntegrity, 64 * 1024 * 1024));

            XNamespace blockNamespace = BlockMapNamespace;
            var blockDocument = ReadXml(blockMap);
            if (blockDocument.Root?.Name != blockNamespace + "BlockMap" ||
                (string?)blockDocument.Root.Attribute("HashMethod") != "http://www.w3.org/2001/04/xmlenc#sha256")
                throw new InvalidDataException("Expected a SHA-256 AppxBlockMap.");
            var mappedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int fileCount = 0;
            long payloadBytes = 0;
            foreach (var file in blockDocument.Root.Elements(blockNamespace + "File"))
            {
                var name = NormalizePath((string?)file.Attribute("Name")
                    ?? throw new InvalidDataException("A block-map file has no name."));
                if (!mappedNames.Add(name))
                    throw new InvalidDataException("Duplicate block-map path: " + name);
                var entry = RequireEntry(entries, name);
                if (!long.TryParse((string?)file.Attribute("Size"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var size) || size < 0 || size != entry.Length)
                    throw new InvalidDataException("Payload size mismatch: " + name);
                ValidateBlocks(entry, file, blockNamespace, size);
                payloadBytes = checked(payloadBytes + size);
                fileCount++;
            }
            if (!mappedNames.Contains("AppxManifest.xml"))
                throw new InvalidDataException("The manifest is not protected by the signed block map.");
            foreach (var name in entries.Keys)
            {
                if (mappedNames.Contains(name) || name.Equals("AppxBlockMap.xml", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("AppxSignature.p7x", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("AppxMetadata/CodeIntegrity.cat", StringComparison.OrdinalIgnoreCase)) continue;
                throw new InvalidDataException("A package payload is absent from the signed block map: " + name);
            }

            var manifest = ReadXml(ReadBytes(RequireEntry(entries, "AppxManifest.xml"), 8 * 1024 * 1024));
            XNamespace manifestNamespace = ManifestNamespace;
            var identity = manifest.Root?.Element(manifestNamespace + "Identity")
                ?? throw new InvalidDataException("The package identity is missing.");
            var publisher = (string?)identity.Attribute("Publisher")
                ?? throw new InvalidDataException("The package publisher is missing.");
            var normalizedPublisher = new X500DistinguishedName(publisher).Name;
            if (!string.Equals(signer.SubjectName.Name, normalizedPublisher, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The manifest publisher does not match the signing certificate.");

            bool selfSigned = signer.SubjectName.RawData.AsSpan().SequenceEqual(signer.IssuerName.RawData);
            string certificateStatus = "Not requested (content-only verification)";
            if (certificatePath is not null)
            {
                using var expectedCertificate = new X509Certificate2(File.ReadAllBytes(certificatePath));
                if (!CryptographicOperations.FixedTimeEquals(signer.RawData, expectedCertificate.RawData))
                    throw new CryptographicException("The MSIX signer does not match the supplied public certificate.");
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = selfSigned ? X509ChainTrustMode.CustomRootTrust : X509ChainTrustMode.System;
                if (selfSigned) chain.ChainPolicy.CustomTrustStore.Add(expectedCertificate);
                else chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.DisableCertificateDownloads = true;
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningOid));
                if (!chain.Build(signer))
                {
                    var messages = new List<string>();
                    foreach (var status in chain.ChainStatus) messages.Add(status.Status + ": " + status.StatusInformation.Trim());
                    throw new CryptographicException("The signing certificate failed offline chain validation: " + string.Join("; ", messages));
                }
                certificateStatus = selfSigned
                    ? "Verified against the supplied certificate using in-memory custom-root trust"
                    : "Signer matches the supplied certificate; its chain was verified using cached system trust and embedded certificates";
            }

            var windowsTrustError = GetWindowsTrustError(packagePath);
            return new MsixVerificationResult
            {
                PackagePath = packagePath,
                Identity = (string?)identity.Attribute("Name") ?? "",
                Version = (string?)identity.Attribute("Version") ?? "",
                Architecture = (string?)identity.Attribute("ProcessorArchitecture") ?? "",
                Publisher = publisher,
                CertificateThumbprint = signer.Thumbprint,
                IsSelfSigned = selfSigned,
                PayloadFiles = fileCount,
                PayloadBytes = payloadBytes,
                CertificateValidation = certificateStatus,
                WindowsTrustError = windowsTrustError,
                WindowsTrust = windowsTrustError == "0x00000000"
                    ? "Windows Authenticode policy accepted the package. This verifier did not change certificate trust."
                    : windowsTrustError == "0x800B0109" && selfSigned
                        ? "Self-signed certificate is not trusted by Windows; installation requires the recipient to trust the public CER first. No certificate was installed."
                        : "Windows Authenticode policy returned " + windowsTrustError + ". No certificate trust was changed.",
                VerifiedChecks = hasCodeIntegrity
                    ? new[] { "CMS cryptographic signature", "Signed AXBM", "Signed AXCT", "Signed AXCI", "All payload block hashes and sizes", "No unlisted payload", "Publisher and signer match", "Code-signing certificate validity" }
                    : new[] { "CMS cryptographic signature", "Signed AXBM", "Signed AXCT", "All payload block hashes and sizes", "No unlisted payload", "Publisher and signer match", "Code-signing certificate validity" }
            };
        }

        private static Dictionary<string, byte[]> ReadSignedDigestTable(byte[] content)
        {
            var reader = new AsnReader(content, AsnEncodingRules.BER);
            var outer = reader.ReadSequence();
            outer.ReadEncodedValue(); // SpcAttributeTypeAndOptionalValue
            var digestInfo = outer.ReadSequence();
            var algorithm = digestInfo.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != Sha256Oid)
                throw new InvalidDataException("The signature digest algorithm is not SHA-256.");
            if (algorithm.HasData) algorithm.ReadNull();
            algorithm.ThrowIfNotEmpty();
            var table = digestInfo.ReadOctetString();
            digestInfo.ThrowIfNotEmpty();
            outer.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            if (table.Length < 4 || Encoding.ASCII.GetString(table, 0, 4) != "APPX" || (table.Length - 4) % 36 != 0)
                throw new InvalidDataException("The signed APPX digest table is malformed.");
            var digests = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int offset = 4; offset < table.Length; offset += 36)
            {
                var tag = Encoding.ASCII.GetString(table, offset, 4);
                if (tag != "AXPC" && tag != "AXCD" && tag != "AXCT" && tag != "AXBM" && tag != "AXCI")
                    throw new InvalidDataException("Unknown signed APPX digest: " + tag);
                if (!digests.TryAdd(tag, table.AsSpan(offset + 4, 32).ToArray()))
                    throw new InvalidDataException("Duplicate signed APPX digest: " + tag);
            }
            return digests;
        }

        private static void CheckDigest(Dictionary<string, byte[]> digests, string tag, byte[] bytes)
        {
            if (!digests.TryGetValue(tag, out var expected) ||
                !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(bytes)))
                throw new CryptographicException("The signed " + tag + " digest does not match the package.");
        }

        private static void ValidateBlocks(ZipArchiveEntry entry, XElement file, XNamespace ns, long size)
        {
            using var stream = entry.Open();
            var buffer = new byte[65536];
            long consumed = 0;
            int blockCount = 0;
            foreach (var block in file.Elements(ns + "Block"))
            {
                if (consumed == size && (size > 0 || blockCount > 0))
                    throw new InvalidDataException("Too many blocks for " + entry.FullName);
                var length = (int)Math.Min(buffer.Length, size - consumed);
                int read = 0;
                while (read < length)
                {
                    int count = stream.Read(buffer, read, length - read);
                    if (count == 0) throw new InvalidDataException("Truncated payload: " + entry.FullName);
                    read += count;
                }
                var expected = Convert.FromBase64String((string?)block.Attribute("Hash")
                    ?? throw new InvalidDataException("A block has no SHA-256 hash."));
                if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(buffer.AsSpan(0, length))))
                    throw new CryptographicException("Payload block hash mismatch: " + entry.FullName + " block " + blockCount);
                consumed += length;
                blockCount++;
            }
            if (consumed != size || stream.ReadByte() != -1)
                throw new InvalidDataException("The block map does not cover the complete payload: " + entry.FullName);
        }

        private static void CheckCertificatePurposeAndDates(X509Certificate2 certificate)
        {
            var now = DateTime.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                throw new CryptographicException("The signing certificate is not currently valid.");
            bool codeSigning = false;
            bool digitalSignature = false;
            foreach (var extension in certificate.Extensions)
            {
                if (extension is X509EnhancedKeyUsageExtension eku)
                    foreach (var oid in eku.EnhancedKeyUsages)
                        if (oid.Value == CodeSigningOid) codeSigning = true;
                if (extension is X509KeyUsageExtension usage)
                    digitalSignature = (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0;
            }
            if (!codeSigning || !digitalSignature)
                throw new CryptographicException("The signing certificate must permit code signing and digital signatures.");
        }

        private static string NormalizePath(string value)
        {
            var name = value.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith("/", StringComparison.Ordinal) || name.IndexOf(':') >= 0)
                throw new InvalidDataException("Invalid package path: " + value);
            foreach (var part in name.Split('/'))
                if (part.Length == 0 || part == "." || part == "..")
                    throw new InvalidDataException("Invalid package path: " + value);
            return name;
        }

        private static ZipArchiveEntry RequireEntry(Dictionary<string, ZipArchiveEntry> entries, string name) =>
            entries.TryGetValue(name, out var entry) ? entry : throw new InvalidDataException("Missing package file: " + name);

        private static byte[] ReadBytes(ZipArchiveEntry entry, long maximumBytes)
        {
            if (entry.Length > maximumBytes) throw new InvalidDataException("Unexpectedly large package metadata: " + entry.FullName);
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        private static XDocument ReadXml(byte[] bytes)
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(xml, LoadOptions.None);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData trustData);
    }
}
