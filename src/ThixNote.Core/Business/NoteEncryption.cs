using System.Security.Cryptography;
using System.Text;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>
/// Password based encryption for .txts files.
/// Layout: magic "QNSEC1" | iterations (int32) | salt (16) | nonce (12) | tag (16) | ciphertext.
/// Key derivation: PBKDF2-SHA256, cipher: AES-256-GCM. Every save uses a fresh random nonce.
/// </summary>
public static class NoteEncryption
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int DefaultIterations = 600_000;
    private static readonly byte[] Magic = "QNSEC1"u8.ToArray();
    private static readonly int HeaderSize = Magic.Length + sizeof(int) + SaltSize + NonceSize + TagSize;

    /// <summary>Creates a new key with a random salt (used when a note gets a new password).</summary>
    public static EncryptionKey CreateKey(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Das Kennwort darf nicht leer sein.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new EncryptionKey(salt, DeriveKey(password, salt, DefaultIterations), DefaultIterations);
    }

    public static byte[] Encrypt(string plainText, EncryptionKey key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key.Key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag, Magic);
        }

        using var stream = new MemoryStream(HeaderSize + cipherBytes.Length);
        stream.Write(Magic);
        stream.Write(BitConverter.GetBytes(key.Iterations));
        stream.Write(key.Salt);
        stream.Write(nonce);
        stream.Write(tag);
        stream.Write(cipherBytes);
        return stream.ToArray();
    }

    public static byte[] Encrypt(string plainText, string password) => Encrypt(plainText, CreateKey(password));

    /// <summary>Decrypts with a password and returns the content plus the derived key for later saves.</summary>
    public static (string Content, EncryptionKey Key) Decrypt(byte[] fileContent, string password)
    {
        var header = ReadHeader(fileContent);
        var key = new EncryptionKey(header.Salt, DeriveKey(password, header.Salt, header.Iterations), header.Iterations);
        return (DecryptPayload(fileContent, header, key.Key), key);
    }

    /// <summary>Decrypts with an already derived key (an unlocked note).</summary>
    public static string Decrypt(byte[] fileContent, EncryptionKey key)
    {
        var header = ReadHeader(fileContent);
        if (!header.Salt.AsSpan().SequenceEqual(key.Salt))
        {
            throw new InvalidPasswordException();
        }

        return DecryptPayload(fileContent, header, key.Key);
    }

    public static bool LooksEncrypted(byte[] fileContent)
        => fileContent.Length >= Magic.Length && fileContent.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    private static FileHeader ReadHeader(byte[] fileContent)
    {
        if (fileContent.Length < HeaderSize || !LooksEncrypted(fileContent))
        {
            throw new InvalidNoteFileException("Keine gültige verschlüsselte ThixNote-Datei.");
        }

        var offset = Magic.Length;
        var iterations = BitConverter.ToInt32(fileContent, offset);
        offset += sizeof(int);
        var salt = fileContent.AsSpan(offset, SaltSize).ToArray();
        offset += SaltSize;
        var nonce = fileContent.AsSpan(offset, NonceSize).ToArray();
        offset += NonceSize;
        var tag = fileContent.AsSpan(offset, TagSize).ToArray();
        return new FileHeader(iterations, salt, nonce, tag);
    }

    private static string DecryptPayload(byte[] fileContent, FileHeader header, byte[] key)
    {
        var cipherBytes = fileContent.AsSpan(HeaderSize);
        var plainBytes = new byte[cipherBytes.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(header.Nonce, cipherBytes, header.Tag, plainBytes, Magic);
        }
        catch (CryptographicException)
        {
            throw new InvalidPasswordException();
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private sealed record FileHeader(int Iterations, byte[] Salt, byte[] Nonce, byte[] Tag);
}
