using System;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace CefSharp.BrowserSubprocess.Features
{
    /// <summary>
    /// HWIDActivator v3.1 — Offline HWID reader for CefSharp.BrowserSubprocess.
    ///
    /// This version runs INSIDE SEB's kiosk (no internet). It only:
    ///   1. Computes machine HWID (SMBIOS UUID + BIOS Serial + Baseboard Serial → SHA256 → Base64)
    ///   2. Reads encrypted .hwid file from disk
    ///   3. Compares → match = activated, mismatch/no file = disabled
    ///
    /// KeyAuth login + HWID binding is handled by PreLauncher.exe (runs before SEB).
    /// This class has NO network code, NO KeyAuth code, NO portal code.
    /// </summary>
    public class HWIDActivator
    {
        // App secret for decrypting the HWID file — must match PreLauncher's secret.
        // Obfuscated in the final build via ConfuserEx.
        private const string APP_SECRET = "2c9c148f1fd79996326928be7cabaa2f5a62d73bf4767f3241a1832451e775c4";

        // Path to the encrypted HWID file — shared with PreLauncher.exe
        private static readonly string HwidFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CefSharp", ".hwid");

        private string cachedHwid;
        private bool? cachedResult;

        /// <summary>Whether the stored HWID file exists on disk.</summary>
        public bool IsBound
        {
            get { return File.Exists(HwidFilePath); }
        }

        /// <summary>
        /// Offline activation check. Reads .hwid file, decrypts, compares with machine HWID.
        /// No network. No KeyAuth. Pure local file + hardware check.
        /// </summary>
        public bool CheckActivation()
        {
            if (cachedResult.HasValue) return cachedResult.Value;

            string machineHwid = Compute();
            string storedHwid = ReadStoredHwid();

            if (storedHwid == null)
            {
                Log("No stored HWID file — device not bound");
                cachedResult = false;
                return false;
            }

            if (string.Equals(storedHwid, machineHwid, StringComparison.Ordinal))
            {
                Log("HWID match — activated (offline)");
                cachedResult = true;
                return true;
            }
            else
            {
                Log("HWID mismatch — stored does not match this machine");
                cachedResult = false;
                return false;
            }
        }

        /// <summary>
        /// Computes machine HWID: SMBIOS UUID + BIOS Serial + Baseboard Serial → SHA256 → Base64.
        /// </summary>
        public string Compute()
        {
            if (!string.IsNullOrEmpty(cachedHwid)) return cachedHwid;

            var raw = new StringBuilder();

            // 1. SMBIOS UUID
            string smbiosUuid = GetWmiProperty("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID");
            if (!string.IsNullOrWhiteSpace(smbiosUuid))
            {
                raw.Append(smbiosUuid.Trim());
            }
            else
            {
                smbiosUuid = GetWmiProperty("SELECT UUID FROM Win32_ComputerSystem", "UUID");
                raw.Append(smbiosUuid?.Trim() ?? "");
            }

            raw.Append("|");

            // 2. BIOS Serial
            string biosSerial = GetWmiProperty("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber");
            raw.Append(biosSerial?.Trim() ?? "");

            raw.Append("|");

            // 3. Baseboard Serial
            string baseboardSerial = GetWmiProperty("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber");
            raw.Append(baseboardSerial?.Trim() ?? "");

            // SHA256 → Base64
            string combined = raw.ToString();
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
                cachedHwid = Convert.ToBase64String(hash);
            }

            return cachedHwid;
        }

        /// <summary>
        /// Returns raw component values for debugging.
        /// </summary>
        public Tuple<string, string, string> GetRawComponents()
        {
            string uuid = GetWmiProperty("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID");
            if (string.IsNullOrWhiteSpace(uuid))
                uuid = GetWmiProperty("SELECT UUID FROM Win32_ComputerSystem", "UUID") ?? "";

            string bios = GetWmiProperty("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber") ?? "";
            string board = GetWmiProperty("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber") ?? "";

            return Tuple.Create(uuid?.Trim() ?? "", bios.Trim(), board.Trim());
        }

        /// <summary>
        /// Derives a 32-byte AES key from the APP_SECRET.
        /// </summary>
        private byte[] DeriveKey()
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(Encoding.UTF8.GetBytes(APP_SECRET));
            }
        }

        /// <summary>
        /// Reads and decrypts the stored HWID from disk.
        /// Returns null if file doesn't exist or decryption fails.
        /// </summary>
        private string ReadStoredHwid()
        {
            try
            {
                if (!File.Exists(HwidFilePath)) return null;

                byte[] fileData = File.ReadAllBytes(HwidFilePath);
                if (fileData.Length < 17) return null;

                byte[] key = DeriveKey();
                byte[] iv = new byte[16];
                byte[] ciphertext = new byte[fileData.Length - 16];

                Buffer.BlockCopy(fileData, 0, iv, 0, 16);
                Buffer.BlockCopy(fileData, 16, ciphertext, 0, ciphertext.Length);

                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var decryptor = aes.CreateDecryptor())
                    {
                        byte[] plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                        return Encoding.UTF8.GetString(plaintext);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("ReadStoredHwid error: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Returns display info for the debug console.
        /// </summary>
        public string GetDisplayInfo()
        {
            var sb = new StringBuilder();
            var raw = GetRawComponents();
            string uuid = raw.Item1;
            string bios = raw.Item2;
            string board = raw.Item3;

            sb.AppendLine("=== HWID Activation Info ===");
            sb.AppendLine("  HWID (Base64): " + Compute());
            sb.AppendLine("  Activated:     " + (CheckActivation() ? "YES ✓" : "NO ✗"));
            sb.AppendLine("  Bound:         " + (IsBound ? "YES (file exists)" : "NO (not bound)"));
            sb.AppendLine("  HWID File:     " + HwidFilePath);
            sb.AppendLine();
            sb.AppendLine("  ── Raw Components ──");
            sb.AppendLine("  SMBIOS UUID:       " + (string.IsNullOrEmpty(uuid) ? "(empty)" : uuid));
            sb.AppendLine("  BIOS Serial:       " + (string.IsNullOrEmpty(bios) ? "(empty)" : bios));
            sb.AppendLine("  Baseboard Serial:  " + (string.IsNullOrEmpty(board) ? "(empty)" : board));

            if (!IsBound)
            {
                sb.AppendLine();
                sb.AppendLine("  ⚠ Device not bound — run PreLauncher.exe before SEB.");
            }

            return sb.ToString();
        }

        private string GetWmiProperty(string query, string property)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (var obj in searcher.Get())
                    {
                        var value = obj[property]?.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }
            }
            catch { }
            return null;
        }

        private void Log(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "seb_v8_debug.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [HWID] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
