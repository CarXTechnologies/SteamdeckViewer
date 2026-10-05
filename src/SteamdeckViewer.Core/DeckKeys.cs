using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SteamdeckViewer.Core;

// SSH-ключ приложения. Как и у SteamOS Devkit Client, это RSA 2048 без пароля:
// публичная часть отправляется на Deck при сопряжении, приватная остаётся только у текущего пользователя Windows
public static class DeckKeys
{
	public static readonly string KeysDirectory = System.IO.Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamdeckViewer", "keys");

	public static readonly string PrivateKeyPath = System.IO.Path.Combine(KeysDirectory, "deck_rsa");
	public static readonly string PublicKeyPath = PrivateKeyPath + ".pub";

	// Ключ официального SteamOS Devkit Client: если Deck уже сопряжён им, подключаемся без повторного сопряжения
	public static readonly string ValveKeyPath = System.IO.Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "steamos-devkit", "steamos-devkit", "devkit_rsa");

	public static string EnsureKeyPair()
	{
		if (File.Exists(PrivateKeyPath) && File.Exists(PublicKeyPath))
		{
			return File.ReadAllText(PublicKeyPath).Trim();
		}

		Directory.CreateDirectory(KeysDirectory);
		DeleteIfExists(PrivateKeyPath);
		DeleteIfExists(PublicKeyPath);

		using RSA rsa = RSA.Create(2048);
		File.WriteAllText(PrivateKeyPath, rsa.ExportRSAPrivateKeyPem() + "\n");
		RestrictToCurrentUser(PrivateKeyPath);

		string publicKey = PublicKeyLine(rsa, $"steamdeckviewer:{Environment.UserName}@{Environment.MachineName}");
		File.WriteAllText(PublicKeyPath, publicKey + "\n");
		return publicKey;
	}

	internal static string PublicKeyLine(RSA rsa, string comment)
	{
		return $"ssh-rsa {Convert.ToBase64String(BuildPublicKeyBlob(rsa.ExportParameters(false)))} {comment}";
	}

	// Ключи по порядку попытки входа: свой, затем ключ Valve
	public static IReadOnlyList<string> AvailablePrivateKeys()
	{
		var keys = new List<string>();
		if (File.Exists(PrivateKeyPath))
		{
			keys.Add(PrivateKeyPath);
		}

		if (File.Exists(ValveKeyPath))
		{
			keys.Add(ValveKeyPath);
		}

		return keys;
	}

	// Формат ssh-rsa: string "ssh-rsa", mpint e, mpint n (RFC 4253, 6.6)
	private static byte[] BuildPublicKeyBlob(RSAParameters parameters)
	{
		using var stream = new MemoryStream();
		WriteBytes(stream, Encoding.ASCII.GetBytes("ssh-rsa"));
		WriteBytes(stream, ToMpint(parameters.Exponent!));
		WriteBytes(stream, ToMpint(parameters.Modulus!));
		return stream.ToArray();
	}

	private static byte[] ToMpint(byte[] value)
	{
		int start = 0;
		while (start < value.Length - 1 && value[start] == 0)
		{
			start++;
		}

		byte[] trimmed = value[start..];
		return (trimmed[0] & 0x80) != 0 ? [0, .. trimmed] : trimmed;
	}

	private static void WriteBytes(Stream stream, byte[] data)
	{
		Span<byte> length = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
		stream.Write(length);
		stream.Write(data);
	}

	// ssh.exe из Windows отказывается работать с ключом, который могут читать другие учётные записи
	internal static void RestrictToCurrentUser(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			RestrictToCurrentUserWindows(path);
		}
		else
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	[SupportedOSPlatform("windows")]
	private static void RestrictToCurrentUserWindows(string path)
	{
		// Владельца не меняем (для этого нужны права администратора): как и icacls /Inheritance:r /Grant:r у Valve,
		// убираем унаследованные разрешения и оставляем доступ только текущему пользователю
		SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
		var security = new FileSecurity();
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
		security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
		new FileInfo(path).SetAccessControl(security);
	}

	private static void DeleteIfExists(string path)
	{
		if (File.Exists(path))
		{
			File.SetAttributes(path, FileAttributes.Normal);
			File.Delete(path);
		}
	}
}
