using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SteamdeckViewer.Core;

public enum MoonlightHostFix
{
	Keep,

	// Прежний Sunshine этого же Deck (то же имя): после переустановки у Sunshine новые сертификат и uuid, запись мертва
	Remove,

	// Другой компьютер, у которого раньше был этот адрес (DHCP): его сопряжение не трогаем, только забываем адрес
	ForgetAddress
}

public sealed record MoonlightHost(int Index, string Name, string Uuid, IReadOnlyList<string> Addresses);

// Записи компьютеров в настройках Moonlight на ПК (QSettings в реестре: hosts\size и hosts\1…N).
// «moonlight pair/stream <адрес>» ищет компьютер по имени, uuid или адресу и ждёт, пока найденная запись будет в сети.
// Если по адресу Deck записана запись с другим uuid (Sunshine переустановили — у него новый uuid), Moonlight
// игнорирует ответы Deck как чужие, ждёт до таймаута и не сопрягается: новую запись он не заводит, раз адрес уже знает
[SupportedOSPlatform("windows")]
public static class MoonlightHosts
{
	public const string RegistryPath = @"Software\Moonlight Game Streaming Project\Moonlight";
	private static readonly string[] AddressValues = ["localaddress", "manualaddress", "remoteaddress", "ipv6address"];

	public static MoonlightHostFix Decide(MoonlightHost host, string deckAddress, string sunshineName, string sunshineUuid)
	{
		if (string.Equals(host.Uuid, sunshineUuid, StringComparison.OrdinalIgnoreCase) ||
		    !host.Addresses.Contains(deckAddress, StringComparer.OrdinalIgnoreCase))
		{
			return MoonlightHostFix.Keep;
		}

		return string.Equals(host.Name, sunshineName, StringComparison.OrdinalIgnoreCase) ? MoonlightHostFix.Remove : MoonlightHostFix.ForgetAddress;
	}

	// Что мешает Moonlight найти Sunshine на deckAddress; ничего не меняет
	public static IReadOnlyList<(MoonlightHost Host, MoonlightHostFix Fix)> FindStale(RegistryKey moonlight, string deckAddress, string sunshineName, string sunshineUuid)
	{
		return ReadHosts(moonlight)
			.Select(h => ToHost(h.Index, h.Tree))
			.Select(host => (Host: host, Fix: Decide(host, deckAddress, sunshineName, sunshineUuid)))
			.Where(h => h.Fix != MoonlightHostFix.Keep)
			.ToList();
	}

	// Убирает мешающие записи и перенумеровывает остальные. Moonlight должен быть закрыт: при выходе он перезаписывает настройки
	public static IReadOnlyList<(MoonlightHost Host, MoonlightHostFix Fix)> Fix(RegistryKey moonlight, string deckAddress, string sunshineName, string sunshineUuid)
	{
		List<(int Index, Tree Tree)> hosts = ReadHosts(moonlight);
		var fixes = new List<(MoonlightHost, MoonlightHostFix)>();
		var kept = new List<Tree>();
		foreach ((int index, Tree tree) in hosts)
		{
			MoonlightHost host = ToHost(index, tree);
			MoonlightHostFix fix = Decide(host, deckAddress, sunshineName, sunshineUuid);
			if (fix != MoonlightHostFix.Keep)
			{
				fixes.Add((host, fix));
			}

			if (fix == MoonlightHostFix.Remove)
			{
				continue;
			}

			if (fix == MoonlightHostFix.ForgetAddress)
			{
				foreach (string name in AddressValues)
				{
					if (tree.Values.FindIndex(v => v.Name == name && string.Equals(v.Value as string, deckAddress, StringComparison.OrdinalIgnoreCase)) is var i and >= 0)
					{
						tree.Values[i] = (name, string.Empty, RegistryValueKind.String);
					}
				}
			}

			kept.Add(tree);
		}

		if (fixes.Count == 0)
		{
			return fixes;
		}

		using RegistryKey hostsKey = moonlight.OpenSubKey("hosts", writable: true)!;
		RegistryValueKind sizeKind = hostsKey.GetValueKind("size");
		foreach ((int index, _) in hosts)
		{
			hostsKey.DeleteSubKeyTree(index.ToString(), throwOnMissingSubKey: false);
		}

		for (int i = 0; i < kept.Count; i++)
		{
			using RegistryKey entry = hostsKey.CreateSubKey((i + 1).ToString());
			Write(entry, kept[i]);
		}

		hostsKey.SetValue("size", sizeKind == RegistryValueKind.DWord ? kept.Count : (object)(long)kept.Count, sizeKind);
		return fixes;
	}

	internal sealed record Tree(List<(string Name, object Value, RegistryValueKind Kind)> Values, List<(string Name, Tree Tree)> Children);

	private static List<(int Index, Tree Tree)> ReadHosts(RegistryKey moonlight)
	{
		using RegistryKey? hostsKey = moonlight.OpenSubKey("hosts");
		if (hostsKey == null)
		{
			return [];
		}

		int size = Convert.ToInt32(hostsKey.GetValue("size") ?? 0);
		var hosts = new List<(int, Tree)>();
		for (int i = 1; i <= size; i++)
		{
			using RegistryKey? entry = hostsKey.OpenSubKey(i.ToString());
			if (entry != null)
			{
				hosts.Add((i, Read(entry)));
			}
		}

		return hosts;
	}

	private static MoonlightHost ToHost(int index, Tree tree)
	{
		string Value(string name)
		{
			return tree.Values.FirstOrDefault(v => v.Name == name).Value as string ?? string.Empty;
		}

		return new MoonlightHost(index, Value("hostname"), Value("uuid"), AddressValues.Select(Value).Where(a => a.Length > 0).ToList());
	}

	private static Tree Read(RegistryKey key)
	{
		var values = key.GetValueNames()
			.Select(n => (n, key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(n)))
			.ToList();
		var children = new List<(string, Tree)>();
		foreach (string name in key.GetSubKeyNames())
		{
			using RegistryKey child = key.OpenSubKey(name)!;
			children.Add((name, Read(child)));
		}

		return new Tree(values, children);
	}

	private static void Write(RegistryKey key, Tree tree)
	{
		foreach ((string name, object value, RegistryValueKind kind) in tree.Values)
		{
			key.SetValue(name, value, kind);
		}

		foreach ((string name, Tree child) in tree.Children)
		{
			using RegistryKey sub = key.CreateSubKey(name);
			Write(sub, child);
		}
	}
}
