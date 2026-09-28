using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Provisioning.Tests;

/// <summary>Текст узла по-русски: фраза (<c>$phrase</c>) или строка.</summary>
internal static class PhraseNodes
{
	public static string? Said(this JsonNode? node) => PhraseJsonConverter.FromNode(node)?.ToString();
}
