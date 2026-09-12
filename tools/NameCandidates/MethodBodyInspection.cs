using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace De4dot.NameCandidates;

public static class MethodBodyInspection {
    public static void Write(string input, IEnumerable<string> tokens) {
        string path=Path.GetFullPath(input);
        byte[] bytes=File.ReadAllBytes(path);
        using var module=ModuleDefMD.Load(bytes);
        var methods=tokens.Select(value => {
            uint token=Convert.ToUInt32(value.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?value[2..]:value,16);
            return module.ResolveToken(token) as MethodDef ?? throw new ArgumentException("Not a method token: "+value);
        }).ToArray();
        object Operand(object value) => value switch {
            null => null,
            Instruction target => new { BranchTarget=target.Offset },
            IList<Instruction> targets => new { BranchTargets=targets.Select(t=>t.Offset).ToArray() },
            IMDTokenProvider member => new { Token="0x"+member.MDToken.Raw.ToString("X8"), Text=value.ToString() },
            Parameter parameter => new { ArgumentIndex=parameter.Index, Name=parameter.Name, Type=parameter.Type.FullName },
            Local local => new { LocalSlot=local.Index, Type=local.Type.FullName },
            IFormattable formatted => formatted.ToString(null,System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        Console.WriteLine(JsonSerializer.Serialize(new {
            Input=path, Identity=module.Assembly?.FullName, Mvid=module.Mvid,
            Sha256=Convert.ToHexString(SHA256.HashData(bytes)),
            Methods=methods.Select(m=>new {
                Token="0x"+m.MDToken.Raw.ToString("X8"), Signature=m.FullName,
                Flags=m.Attributes.ToString(), m.HasBody,
                Parameters=m.Parameters.Select(p=>new { ArgumentIndex=p.Index, Sequence=p.IsNormalMethodParameter?(int?)(p.MethodSigIndex+1):null, Name=p.Name, Type=p.Type.FullName }).ToArray(),
                Locals=m.Body?.Variables.Select(v=>new { Slot=v.Index, Type=v.Type.FullName }).ToArray(),
                Instructions=m.Body?.Instructions.Select(i=>new { i.Offset, OpCode=i.OpCode.Name, Operand=Operand(i.Operand) }).ToArray(),
                ExceptionHandlers=m.Body?.ExceptionHandlers.Select(e=>new { Kind=e.HandlerType.ToString(),
                    TryStart=e.TryStart?.Offset, TryEnd=e.TryEnd?.Offset, HandlerStart=e.HandlerStart?.Offset,
                    HandlerEnd=e.HandlerEnd?.Offset, FilterStart=e.FilterStart?.Offset, CatchType=e.CatchType?.FullName }).ToArray()
            }).ToArray()
        },new JsonSerializerOptions { WriteIndented=true }));
    }
}
