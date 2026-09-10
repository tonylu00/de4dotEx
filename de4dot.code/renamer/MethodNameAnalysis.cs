using System;
using System.Collections.Generic;
using System.Linq;

namespace de4dot.code.renamer {
	// Lexical evidence, not a claim to recover the author's intent. Unknown domain
	// words and non-Latin identifiers defer to the protector-specific name checker.
	public static class MethodNameAnalysis {
		public enum Assessment { Unknown, Meaningful, Obfuscated }
		static readonly HashSet<string> words = new HashSet<string>((
			"get set add remove delete create build load save read write open close start stop run try put map pop push peek " +
			"find search select update insert clear reset restore resolve convert parse format encode decode serialize deserialize " +
			"validate verify check compare equals hash invoke execute process handle on begin end async await task result value item " +
			"name type method property field event parameter argument count length index key access certificate project file directory " +
			"path stream buffer byte bytes string text data object list array collection dictionary request response message connection " +
			"connect disconnect send receive initialize dispose release acquire lock unlock register unregister subscribe unsubscribe " +
			"notify changed change default current next previous first last all any min max sum abs log sin cos tan copy clone to from " +
			"is has can should with without of for by as at in out not null empty true false error exception success failure " +
			"http https tcp udp ip uri url xml json sql sdk api ui io id guid uuid utf ascii unicode sha md crc rsa aes x509").Split(' '), StringComparer.OrdinalIgnoreCase);
		public static string[] Tokenize(string name) {
			if (string.IsNullOrEmpty(name)) return Array.Empty<string>();
			var tokens = new List<string>();
			int start = 0;
			for (int i = 0; i <= name.Length; i++) {
				bool separator = i == name.Length || name[i] == '_' || name[i] == '.';
				bool boundary = !separator && i > start && (char.IsDigit(name[i]) != char.IsDigit(name[i - 1]) ||
					char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) ||
					char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
				if (!separator && !boundary) continue;
				if (i > start) tokens.Add(name.Substring(start, i - start));
				start = separator ? i + 1 : i;
			}
			return tokens.ToArray();
		}
		public static Assessment Analyze(string name) {
			if (string.IsNullOrEmpty(name) || char.IsDigit(name[0]) || name.Any(c => c > 127 || !char.IsLetterOrDigit(c) && c != '_' && c != '.')) return Assessment.Unknown;
			// Explicit interface qualifications describe the owner, not the method.
			int qualifier = name.LastIndexOf('.');
			if (qualifier >= 0) name = name.Substring(qualifier + 1);
			var tokens = Tokenize(name);
			var letters = tokens.Where(t => !t.All(char.IsDigit)).ToArray();
			int recognized = letters.Where(words.Contains).Sum(t => t.Length);
			int total = letters.Sum(t => t.Length);
			// Preserve common short verbs, while "a"/"ab" overloads still use the
			// protector's short-name rules. Overload counts never enter the decision.
			if (total != 0 && letters.Any(words.Contains) && recognized * 2 >= total) return Assessment.Meaningful;
			if (name.Length < 12 || letters.Length == 0) return Assessment.Unknown;
			if (recognized == 0 && name.Length >= 24 && name.All(c => "0123456789abcdefABCDEF".IndexOf(c) >= 0) && name.Any(char.IsDigit))
				return Assessment.Obfuscated;
			int fragments = letters.Count(t => t.Length <= 2);
			int numberRuns = tokens.Count(t => t.All(char.IsDigit));
			if (recognized * 4 < total && (letters.Length >= 6 && fragments * 4 >= letters.Length * 3 || numberRuns >= 4 && fragments >= 4))
				return Assessment.Obfuscated;
			return Assessment.Unknown;
		}
		public static bool IsValid(string name, INameChecker fallback) {
			switch (Analyze(name)) {
			case Assessment.Meaningful: return true;
			case Assessment.Obfuscated: return false;
			default: return fallback.IsValidMethodName(name);
			}
		}
	}
}
