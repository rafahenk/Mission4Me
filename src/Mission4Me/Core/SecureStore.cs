using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Mission4Me.Core;

/// <summary>
/// Criptografia em repouso dos dados do usuário com DPAPI do Windows (escopo: usuário atual).
///
/// • A chave é derivada da conta do Windows: só o próprio usuário, na própria conta, consegue ler.
///   Protege contra outros usuários do PC, cópia do disco/backup e servidores de perfil móvel (Roaming).
/// • Não protege contra um programa malicioso rodando na SUA conta — nada no espaço do usuário protege.
/// • Arquivos JSON: o arquivo inteiro vira um bloco cifrado ("M4ME" + versão + blob DPAPI).
/// • Arquivos de linhas (CSV / JSONL): cada linha é cifrada separadamente (base64), para continuar
///   permitindo acrescentar uma linha sem reescrever o arquivo.
/// • Arquivos antigos em texto puro são lidos normalmente e cifrados na próxima gravação.
/// </summary>
public static class SecureStore
{
    private static readonly byte[] Magic = { (byte)'M', (byte)'4', (byte)'M', (byte)'E', 1 };
    private const string LineHeader = "#mission4me-enc:1";
    // Entropia fixa: não é segredo (o código é público); só separa os dados deste app de outros blobs DPAPI.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mission4Me/dados/v1");
    private static readonly UTF8Encoding Utf8 = new(false);

    // ------------------------------------------------------------- arquivo inteiro
    public static string? ReadAllText(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= Magic.Length && bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return Utf8.GetString(Unprotect(bytes.AsSpan(Magic.Length).ToArray()));
        return Utf8.GetString(bytes).TrimStart('﻿');   // legado em texto puro
    }

    public static void WriteAllText(string path, string text)
    {
        var blob = Protect(Utf8.GetBytes(text));
        var data = new byte[Magic.Length + blob.Length];
        Magic.CopyTo(data, 0);
        blob.CopyTo(data, Magic.Length);
        WriteAtomic(path, data);
    }

    // ------------------------------------------------------------- arquivo de linhas
    public static List<string> ReadLines(string path)
    {
        var result = new List<string>();
        if (!File.Exists(path)) return result;
        var raw = File.ReadAllLines(path, Utf8);
        if (raw.Length > 0 && raw[0] == LineHeader)
        {
            foreach (var l in raw.Skip(1))
                if (l.Length > 0) result.Add(Utf8.GetString(Unprotect(Convert.FromBase64String(l))));
        }
        else
        {
            foreach (var l in raw) result.Add(l.TrimStart('﻿'));   // legado em texto puro
        }
        return result;
    }

    public static void WriteLines(string path, IEnumerable<string> lines)
    {
        var sb = new StringBuilder().Append(LineHeader).Append('\n');
        foreach (var l in lines) sb.Append(Convert.ToBase64String(Protect(Utf8.GetBytes(l)))).Append('\n');
        WriteAtomic(path, Utf8.GetBytes(sb.ToString()));
    }

    public static void AppendLine(string path, string line)
    {
        if (!File.Exists(path) || !IsEncryptedLineFile(path))
        {
            var all = ReadLines(path);   // vazio ou legado em texto puro → converte tudo
            all.Add(line);
            WriteLines(path, all);
            return;
        }
        File.AppendAllText(path, Convert.ToBase64String(Protect(Utf8.GetBytes(line))) + "\n", Utf8);
    }

    public static bool IsEncryptedLineFile(string path)
    {
        using var r = new StreamReader(path, Utf8);
        return r.ReadLine() == LineHeader;
    }

    public static bool IsEncryptedBlobFile(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[Magic.Length];
        return f.Read(head, 0, head.Length) == head.Length && head.SequenceEqual(Magic);
    }

    /// <summary>Cifra, no lugar, arquivos que ainda estejam em texto puro (inclui backups antigos).</summary>
    public static void EncryptLegacyFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("barra-original.json", StringComparison.OrdinalIgnoreCase)) continue; // só cores; precisa ser legível na restauração
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;

                if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsEncryptedLineFile(f)) WriteLines(f, ReadLines(f));
                }
                else if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsEncryptedBlobFile(f)) WriteAllText(f, ReadAllText(f) ?? "");
                }
            }
            catch (Exception ex) { Storage.LogError("EncryptLegacy " + Path.GetFileName(f), ex); }
        }
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, true);
    }

    // ------------------------------------------------------------- DPAPI (crypt32)
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public static byte[] Protect(byte[] plain) => Run(plain, protect: true);
    public static byte[] Unprotect(byte[] cipher) => Run(cipher, protect: false);

    private static byte[] Run(byte[] input, bool protect)
    {
        var inH = GCHandle.Alloc(input, GCHandleType.Pinned);
        var enH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = inH.AddrOfPinnedObject() };
            var enBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = enH.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Mission4Me", ref enBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref enBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            inH.Free();
            enH.Free();
        }
    }
}
