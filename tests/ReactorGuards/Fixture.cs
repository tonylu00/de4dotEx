using System;

class Fixture {
	// .NET Reactor embeds these message fragments in its tamper/debug guards.
	// de4dotEx must neutralize the guard bodies and remove the calls.
	static void TamperGuard() { Console.WriteLine("guard:is tampered"); }
	static void DebuggerGuard() { Console.WriteLine("debug:Debugger Detected"); }

	static int Main() {
		try {
			TamperGuard();
			DebuggerGuard();
			Console.WriteLine("PASS");
			return 0;
		} catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
	}
}
