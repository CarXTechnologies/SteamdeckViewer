using System.Text;
using System.Text.RegularExpressions;

namespace SteamdeckViewer.Core;

// Исключения в духе rsync, через пробел или с новой строки:
//   "*.pdb"        — имя файла или папки на любой глубине
//   "Logs/"        — только папка, на любой глубине
//   "Data/Raw/*"   — путь от корня билда (есть "/" внутри)
public sealed class FileFilter
{
	private readonly List<Rule> m_rules = [];

	public FileFilter(string patterns)
	{
		foreach (string raw in patterns.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
		{
			string pattern = raw.Replace('\\', '/');
			bool directoryOnly = pattern.EndsWith('/');
			pattern = pattern.Trim('/');
			if (pattern.Length == 0)
			{
				continue;
			}

			m_rules.Add(new Rule(new Regex("^" + GlobToRegex(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
				directoryOnly, pattern.Contains('/')));
		}
	}

	public bool IsExcluded(string relativePath)
	{
		if (m_rules.Count == 0)
		{
			return false;
		}

		string[] segments = relativePath.Split('/');
		foreach (Rule rule in m_rules)
		{
			if (rule.Anchored)
			{
				// Папка-шаблон от корня исключает всё внутри неё
				for (int i = 1; i <= segments.Length; i++)
				{
					bool isDirectory = i < segments.Length;
					if ((!rule.DirectoryOnly || isDirectory) && rule.Regex.IsMatch(string.Join('/', segments, 0, i)))
					{
						return true;
					}
				}

				continue;
			}

			for (int i = 0; i < segments.Length; i++)
			{
				bool isDirectory = i < segments.Length - 1;
				if ((!rule.DirectoryOnly || isDirectory) && rule.Regex.IsMatch(segments[i]))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static string GlobToRegex(string glob)
	{
		var sb = new StringBuilder();
		for (int i = 0; i < glob.Length; i++)
		{
			char c = glob[i];
			if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
			{
				sb.Append(".*");
				i++;
			}
			else
			{
				sb.Append(c switch
				{
					'*' => "[^/]*",
					'?' => "[^/]",
					_ => Regex.Escape(c.ToString())
				});
			}
		}

		return sb.ToString();
	}

	private sealed record Rule(Regex Regex, bool DirectoryOnly, bool Anchored);
}
