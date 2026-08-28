using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Game.Framework.Build
{
    /// <summary>
    /// 读取会影响 HybridCLR Link、AOT 泛型、MethodBridge、P/Invoke 与反向 P/Invoke 的 Player 元数据拓扑。
    /// </summary>
    /// <remarks>
    /// 实现与 HybridCLR Editor Module 复用同一份 <c>dnlib.dll</c>，依赖不会泄漏到通用 Framework Editor。
    /// 结构定义和 IL 中的元数据操作数会进入指纹；普通算术、分支、常量与字符串字面量不会进入，因此纯算法
    /// 修改仍可走快速 CompileDll。模块从字节数组读取并立即释放，不持有 ScriptAssemblies 文件句柄。
    /// </remarks>
    internal static class FrameworkPlayerMetadataTopology
    {
        private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new();

        internal static string[] ReadEntries(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("程序集路径不能为空。", nameof(path));
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("无法读取 Player 元数据拓扑：文件不存在。", fullPath);
            var file = new FileInfo(fullPath);
            lock (CacheLock)
            {
                if (Cache.TryGetValue(fullPath, out CacheEntry cached) &&
                    cached.Length == file.Length && cached.LastWriteUtc == file.LastWriteTimeUtc)
                    return cached.Entries;
            }

            try
            {
                using ModuleDefMD module = ModuleDefMD.Load(File.ReadAllBytes(fullPath));
                string[] entries = Collect(module).OrderBy(entry => entry, StringComparer.Ordinal).ToArray();
                lock (CacheLock)
                    Cache[fullPath] = new CacheEntry(file.Length, file.LastWriteTimeUtc, entries);
                return entries;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"无法读取 Player 元数据拓扑：{fullPath}", exception);
            }
        }

        private static IEnumerable<string> Collect(ModuleDefMD module)
        {
            var entries = new List<string>(4096)
            {
                $"MODULE|{module.Assembly?.FullName}|{module.Name}|{module.Kind}|{module.Machine}|{module.Cor20HeaderFlags}",
            };
            AddAttributes(entries, module.Assembly, "ASSEMBLY");
            AddAttributes(entries, module, "MODULE");

            AddMetadataTables(module, entries);
            foreach (TypeDef type in module.GetTypes()) AddTypeDefinition(entries, type);
            return entries;
        }

        private static void AddMetadataTables(ModuleDefMD module, ICollection<string> entries)
        {
            for (uint rid = 1; rid <= module.Metadata.TablesStream.AssemblyRefTable.Rows; rid++)
                entries.Add("AR|" + module.ResolveAssemblyRef(rid)?.FullName);
            foreach (ExportedType item in module.ExportedTypes)
            {
                string key = $"ET|{item.FullName}|{(uint)item.Attributes}|{item.Implementation}|" +
                             $"definition={item.DefinitionAssembly}|declaring={item.DeclaringType}";
                entries.Add(key);
                AddAttributes(entries, item, key);
            }
            for (uint rid = 1; rid <= module.Metadata.TablesStream.ModuleRefTable.Rows; rid++)
                entries.Add("NR|" + module.ResolveModuleRef(rid)?.FullName);
            for (uint rid = 1; rid <= module.Metadata.TablesStream.TypeRefTable.Rows; rid++)
            {
                TypeRef item = module.ResolveTypeRef(rid);
                entries.Add($"TR|{item?.ResolutionScope}|{item?.FullName}");
                AddAttributes(entries, item, "TR|" + item?.FullName);
            }
            for (uint rid = 1; rid <= module.Metadata.TablesStream.TypeSpecTable.Rows; rid++)
            {
                TypeSpec item = module.ResolveTypeSpec(rid);
                entries.Add("TS|" + item?.TypeSig);
                AddAttributes(entries, item, "TS|" + item?.TypeSig);
            }
            for (uint rid = 1; rid <= module.Metadata.TablesStream.MemberRefTable.Rows; rid++)
            {
                MemberRef item = module.ResolveMemberRef(rid);
                entries.Add($"MR|{item?.Class}|{item?.FullName}|{item?.Signature}");
                AddAttributes(entries, item, "MR|" + item?.FullName);
            }
            for (uint rid = 1; rid <= module.Metadata.TablesStream.MethodSpecTable.Rows; rid++)
            {
                MethodSpec item = module.ResolveMethodSpec(rid);
                entries.Add($"MS|{item?.Method}|{item?.Instantiation}");
                AddAttributes(entries, item, "MS|" + item?.FullName);
            }
            for (uint rid = 1; rid <= module.Metadata.TablesStream.StandAloneSigTable.Rows; rid++)
            {
                StandAloneSig item = module.ResolveStandAloneSig(rid);
                entries.Add("SA|" + item?.Signature);
                AddAttributes(entries, item, "SA|" + item?.Signature);
            }
        }

        private static void AddTypeDefinition(ICollection<string> entries, TypeDef type)
        {
            string owner = type.FullName;
            entries.Add($"TD|{owner}|{(uint)type.Attributes}|{type.BaseType}|{type.Layout}|" +
                        $"pack={type.PackingSize}|size={type.ClassSize}");
            AddAttributes(entries, type, "TD|" + owner);
            AddGenericParameters(entries, type.GenericParameters, "TD|" + owner);

            for (int index = 0; index < type.Interfaces.Count; index++)
            {
                InterfaceImpl implementation = type.Interfaces[index];
                string key = $"IF|{owner}|{index}|{implementation.Interface}";
                entries.Add(key);
                AddAttributes(entries, implementation, key);
            }
            for (int index = 0; index < type.Fields.Count; index++)
                AddFieldDefinition(entries, type.Fields[index], owner, index);
            foreach (MethodDef method in type.Methods) AddMethodDefinition(entries, method, owner);
            foreach (PropertyDef property in type.Properties)
            {
                string key = $"PD|{owner}|{property.FullName}|{(uint)property.Attributes}|{property.PropertySig}";
                entries.Add(key);
                AddAttributes(entries, property, key);
            }
            foreach (EventDef eventDefinition in type.Events)
            {
                string key = $"ED|{owner}|{eventDefinition.FullName}|{(uint)eventDefinition.Attributes}|" +
                             eventDefinition.EventType;
                entries.Add(key);
                AddAttributes(entries, eventDefinition, key);
            }
        }

        private static void AddFieldDefinition(
            ICollection<string> entries,
            FieldDef field,
            string owner,
            int index)
        {
            string key = $"FD|{owner}|{index}|{field.FullName}|{(uint)field.Attributes}|{field.FieldSig}|" +
                         $"offset={(field.HasLayoutInfo ? field.FieldOffset.ToString() : "-")}|" +
                         $"marshal={(field.HasMarshalType ? field.MarshalType?.ToString() : "-")}|" +
                         $"constant={(field.HasConstant ? StableValue(field.Constant?.Value) : "-")}";
            entries.Add(key);
            AddAttributes(entries, field, key);
        }

        private static void AddMethodDefinition(ICollection<string> entries, MethodDef method, string owner)
        {
            string key = $"MD|{owner}|{method.FullName}|{(uint)method.Attributes}|{(uint)method.ImplAttributes}|" +
                         $"{method.MethodSig}";
            entries.Add(key);
            AddAttributes(entries, method, key);
            AddGenericParameters(entries, method.GenericParameters, key);

            for (int index = 0; index < method.ParamDefs.Count; index++)
            {
                ParamDef parameter = method.ParamDefs[index];
                string parameterKey = $"PA|{key}|{index}|seq={parameter.Sequence}|{parameter.Name}|" +
                                      $"{(uint)parameter.Attributes}|" +
                                      $"marshal={(parameter.HasMarshalType ? parameter.MarshalType?.ToString() : "-")}|" +
                                      $"constant={(parameter.HasConstant ? StableValue(parameter.Constant?.Value) : "-")}";
                entries.Add(parameterKey);
                AddAttributes(entries, parameter, parameterKey);
            }
            foreach (MethodOverride methodOverride in method.Overrides)
                entries.Add($"OV|{key}|{methodOverride.MethodBody}|{methodOverride.MethodDeclaration}");
            if (method.HasImplMap)
            {
                ImplMap map = method.ImplMap;
                entries.Add($"PI|{key}|{map.Module?.FullName}|{map.Name}|{(uint)map.Attributes}|{map.CallConv}");
            }
            AddMetadataOperands(entries, method, key);
        }

        private static void AddMetadataOperands(ICollection<string> entries, MethodDef method, string owner)
        {
            if (!method.HasBody || method.Body?.Instructions == null) return;
            int dependencyOrdinal = 0;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                string value = instruction.Operand switch
                {
                    IMDTokenProvider token => token.GetType().Name + "|" + token,
                    MethodSig signature => "MethodSig|" + signature,
                    _ => null,
                };
                if (value == null) continue;
                entries.Add($"IL|{owner}|{dependencyOrdinal++}|{instruction.OpCode.Code}|{value}");
            }
        }

        private static void AddGenericParameters(
            ICollection<string> entries,
            IList<GenericParam> parameters,
            string owner)
        {
            for (int index = 0; index < parameters.Count; index++)
            {
                GenericParam parameter = parameters[index];
                string key = $"GP|{owner}|{index}|num={parameter.Number}|{parameter.Name}|{(ushort)parameter.Flags}";
                entries.Add(key);
                AddAttributes(entries, parameter, key);
                for (int constraintIndex = 0;
                     constraintIndex < parameter.GenericParamConstraints.Count;
                     constraintIndex++)
                {
                    GenericParamConstraint constraint = parameter.GenericParamConstraints[constraintIndex];
                    string constraintKey = $"GC|{key}|{constraintIndex}|{constraint.Constraint}";
                    entries.Add(constraintKey);
                    AddAttributes(entries, constraint, constraintKey);
                }
            }
        }

        private static void AddAttributes(ICollection<string> entries, IHasCustomAttribute provider, string owner)
        {
            if (provider == null || !provider.HasCustomAttributes) return;
            for (int index = 0; index < provider.CustomAttributes.Count; index++)
            {
                CustomAttribute attribute = provider.CustomAttributes[index];
                entries.Add($"CA|{owner}|{index}|{SerializeCustomAttribute(attribute)}");
            }
        }

        /// <summary>
        /// dnlib 对正常解析的特性不会填充 <see cref="CustomAttribute.RawData"/>；只记录
        /// <c>ToString()</c> 又会丢失构造参数和命名参数。这里用长度前缀二进制规范化完整参数树，
        /// 仅在 dnlib 明确保留为 raw blob 时退回原始字节。
        /// </summary>
        private static string SerializeCustomAttribute(CustomAttribute attribute)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                WriteText(writer, attribute.Constructor?.ToString());
                if (attribute.IsRawBlob)
                {
                    writer.Write((byte)0);
                    byte[] raw = attribute.RawData ?? Array.Empty<byte>();
                    writer.Write(raw.Length);
                    writer.Write(raw);
                }
                else
                {
                    writer.Write((byte)1);
                    writer.Write(attribute.ConstructorArguments.Count);
                    foreach (CAArgument argument in attribute.ConstructorArguments)
                        WriteCustomAttributeArgument(writer, argument);

                    writer.Write(attribute.NamedArguments.Count);
                    foreach (CANamedArgument argument in attribute.NamedArguments)
                    {
                        writer.Write(argument.IsField);
                        WriteText(writer, argument.Name?.String);
                        WriteText(writer, argument.Type?.ToString());
                        WriteCustomAttributeArgument(writer, argument.Argument);
                    }
                }
            }
            return Convert.ToBase64String(stream.ToArray());
        }

        private static void WriteCustomAttributeArgument(BinaryWriter writer, CAArgument argument)
        {
            WriteText(writer, argument.Type?.ToString());
            WriteCustomAttributeValue(writer, argument.Value);
        }

        private static void WriteCustomAttributeValue(BinaryWriter writer, object value)
        {
            if (value == null)
            {
                writer.Write((byte)0);
                return;
            }

            switch (value)
            {
                case UTF8String text:
                    writer.Write((byte)1);
                    WriteText(writer, text.String);
                    return;
                case TypeSig typeSignature:
                    writer.Write((byte)2);
                    WriteText(writer, typeSignature.ToString());
                    return;
                case ITypeDefOrRef typeReference:
                    writer.Write((byte)3);
                    WriteText(writer, typeReference.ToString());
                    return;
                case byte[] bytes:
                    writer.Write((byte)4);
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                    return;
                case IEnumerable<CAArgument> arguments:
                {
                    writer.Write((byte)5);
                    CAArgument[] array = arguments.ToArray();
                    writer.Write(array.Length);
                    foreach (CAArgument argument in array) WriteCustomAttributeArgument(writer, argument);
                    return;
                }
                default:
                    writer.Write((byte)6);
                    WriteText(writer, value.GetType().FullName);
                    WriteText(writer, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
            }
        }

        private static void WriteText(BinaryWriter writer, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string StableValue(object value)
        {
            if (value == null) return "null";
            if (value is byte[] bytes) return Convert.ToBase64String(bytes);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private sealed class CacheEntry
        {
            internal readonly long Length;
            internal readonly DateTime LastWriteUtc;
            internal readonly string[] Entries;

            internal CacheEntry(long length, DateTime lastWriteUtc, string[] entries)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                Entries = entries;
            }
        }
    }
}
