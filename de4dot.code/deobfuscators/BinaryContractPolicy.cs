namespace de4dot.code.deobfuscators {
	/// <summary>Per-run policy for transformations which change binary signatures.</summary>
	public static class BinaryContractPolicy {
		public const string ContextKey = "de4dot.preserve-binary-signatures";
		public static bool PreserveSignatures(IDeobfuscatedFile file) =>
			file?.DeobfuscatorContext?.GetData(ContextKey) is bool enabled && enabled;
	}
}
