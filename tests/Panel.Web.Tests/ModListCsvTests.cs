using System.Text;
using Panel.Contracts;
using Panel.Contracts.Content;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>Выгрузка перечня модов в CSV.</summary>
public sealed class ModListCsvTests
{
	[Fact]
	public void Файл_в_utf8_с_bom_и_строками_crlf()
	{
		var bytes = ModListCsv.Build(new ModsPayload { Mods = [Mod("a.zip", "alpha")] }, PanelLanguage.English);

		Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
		Assert.Equal(
			"\"Name\",\"Mod ID\",\"Version\",\"Side\",\"Status\",\"Dependencies\",\"Issues\",\"File\",\"Mod page\"\r\n" +
			"\"Alpha\",\"alpha\",\"1.0.0\",\"Universal\",\"On\",\"\",\"\",\"a.zip\",\"https://mods.vintagestory.at/alpha\"\r\n",
			Text(bytes));
	}

	[Fact]
	public void Заголовки_на_языке_интерфейса()
	{
		var csv = Text(ModListCsv.Build(new ModsPayload(), PanelLanguage.Russian));

		Assert.Equal("\"Название\",\"Идентификатор\",\"Версия\",\"Сторона\",\"Состояние\",\"Зависимости\",\"Проблемы\",\"Файл\",\"Страница мода\"\r\n", csv);
	}

	[Fact]
	public void Строки_по_modid_без_учёта_регистра_архив_без_modinfo_по_имени_файла()
	{
		var csv = Text(ModListCsv.Build(new ModsPayload
		{
			Mods = [Mod("z.zip", "Zeta"), new ModEntryPayload { Name = "broken.zip", Enabled = true, Size = 1, Modified = 1 }, Mod("a.zip", "alpha")],
		}, PanelLanguage.English));

		Assert.Equal(["alpha", "broken.zip", "Zeta"], Rows(csv).Skip(1).Select(row => row[7] == "broken.zip" ? row[7] : row[1]));
	}

	[Fact]
	public void Кавычки_запятые_и_переводы_строк_внутри_поля()
	{
		var csv = Text(ModListCsv.Build(new ModsPayload
		{
			Mods = [Mod("a.zip", "alpha") with { DisplayName = "Say \"hi\",\nthen go" }],
		}, PanelLanguage.English));

		Assert.Contains("\"Say \"\"hi\"\",\nthen go\"", csv);
	}

	/// <summary>Название берётся из архива мода: формулу в нём табличный редактор выполнил бы.</summary>
	[Theory]
	[InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"\"http://x\"\")")]
	[InlineData("+1", "'+1")]
	[InlineData("-1", "'-1")]
	[InlineData("@SUM(A1)", "'@SUM(A1)")]
	[InlineData("\tcmd", "'\tcmd")]
	[InlineData("Plain = text", "Plain = text")]
	public void Формула_в_поле_экранируется(string name, string cell)
	{
		var csv = Text(ModListCsv.Build(new ModsPayload { Mods = [Mod("a.zip", "alpha") with { DisplayName = name }] }, PanelLanguage.English));

		Assert.Contains($"\r\n\"{cell}\",", csv);
	}

	[Fact]
	public void Состояние_и_проблемы_как_в_разделе_модов()
	{
		var csv = Text(ModListCsv.Build(new ModsPayload
		{
			Mods =
			[
				Mod("a.zip", "alpha") with
				{
					Dependencies = new Dictionary<string, string> { ["game"] = "1.21.0", ["beta"] = "" },
					DependencyIssues =
					[
						new DependencyIssuePayload { ModId = "beta", Status = "missing", Required = "" },
						new DependencyIssuePayload { ModId = "gamma", Status = "outdated", Required = "2.0.0", Installed = "1.0.0" },
					],
				},
				Mod("b.zip", "beta") with { Enabled = false },
				Mod("c.zip", "dup"),
				Mod("d.zip", "dup"),
				new ModEntryPayload { Name = "e.zip", Enabled = true, Size = 1, Modified = 1, Error = Phrase.From("modinfo.json не найден") },
			],
			DuplicateIds = ["dup"],
		}, PanelLanguage.English));

		var rows = Rows(csv).Skip(1).ToDictionary(row => row[7], row => row);
		Assert.Equal(["Has issues", "beta; game ≥ 1.21.0", "missing beta; gamma: installed 1.0.0, requires ≥ 2.0.0"], rows["a.zip"][4..7]);
		Assert.Equal(["Off", "", ""], rows["b.zip"][4..7]);
		Assert.Equal(["Has issues", "", "modid dup is also used by another archive"], rows["c.zip"][4..7]);
		Assert.Equal("Archive unreadable", rows["e.zip"][4]);
		Assert.Equal("modinfo.json не найден", rows["e.zip"][6]);
		Assert.Equal("", rows["e.zip"][8]);
	}

	private static ModEntryPayload Mod(string file, string id) => new()
	{
		Name = file,
		Enabled = true,
		Size = 1,
		Modified = 1,
		ModId = id,
		DisplayName = char.ToUpperInvariant(id[0]) + id[1..],
		Version = "1.0.0",
		Side = "Universal",
	};

	private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

	/// <summary>Разбор по RFC 4180, достаточный для файлов, где все поля в кавычках.</summary>
	private static List<string[]> Rows(string csv)
	{
		var rows = new List<string[]>();
		var row = new List<string>();
		var cell = new StringBuilder();
		for (var index = 0; index < csv.Length; index++)
		{
			var c = csv[index];
			if (c == '"')
			{
				for (index++; index < csv.Length; index++)
				{
					if (csv[index] == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
					{
						cell.Append('"');
						index++;
					}
					else if (csv[index] == '"')
					{
						break;
					}
					else
					{
						cell.Append(csv[index]);
					}
				}
			}
			else if (c == ',')
			{
				row.Add(cell.ToString());
				cell.Clear();
			}
			else if (c == '\r')
			{
				row.Add(cell.ToString());
				cell.Clear();
				rows.Add([.. row]);
				row.Clear();
				index++;
			}
		}

		return rows;
	}
}
