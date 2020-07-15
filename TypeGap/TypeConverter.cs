using TypeGap.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TypeLite;
using TypeLite.TsModels;

namespace TypeGap
{
    public class TypeConverter
    {
        private readonly string _globalNamespace;
        private readonly TypeScriptFluent _fluent;
        private static readonly Dictionary<Type, (string type, bool nullable)> _cache;
        private readonly Dictionary<Type, string> _customConversions;
        private readonly bool _strictNullChecks;

        static TypeConverter()
        {
            _cache = new Dictionary<Type, (string, bool)>();
            // Integral types
            _cache.Add(typeof(object), ("any", true));
            _cache.Add(typeof(bool), ("boolean", false));
            _cache.Add(typeof(byte), ("number", false));
            _cache.Add(typeof(sbyte), ("number", false));
            _cache.Add(typeof(short), ("number", false));
            _cache.Add(typeof(ushort), ("number", false));
            _cache.Add(typeof(int), ("number", false));
            _cache.Add(typeof(uint), ("number", false));
            _cache.Add(typeof(long), ("number", false));
            _cache.Add(typeof(ulong), ("number", false));
            _cache.Add(typeof(float), ("number", false));
            _cache.Add(typeof(double), ("number", false));
            _cache.Add(typeof(decimal), ("number", false));
            _cache.Add(typeof(string), ("string", true));
            _cache.Add(typeof(char), ("string", false));
            _cache.Add(typeof(DateTime), ("Date", false));
            _cache.Add(typeof(DateTimeOffset), ("Date", false));
            _cache.Add(typeof(byte[]), ("string", true));
            _cache.Add(typeof(Guid), ("string", false));
            _cache.Add(typeof(Exception), ("string", true));
            _cache.Add(typeof(void), ("void", false));
        }

        public TypeConverter(string globalNamespace, TypeScriptFluent fluent, Dictionary<Type, string> customConversions, bool strictNullChecks = false)
        {
            _globalNamespace = globalNamespace;
            _fluent = fluent;
            _customConversions = customConversions;
            _strictNullChecks = strictNullChecks;
        }

        public static bool IsComplexType(Type clrType)
        {
            return !_cache.ContainsKey(clrType);
        }

        private string GetFullName(Type clrType)
        {
            string moduleName = null;

            var tsMember = _fluent.ModelBuilder.GetType(clrType) as TsModuleMember;
            if (tsMember != null)
                moduleName = tsMember.Module.Name + "." + tsMember.Name;

            var fullName = String.IsNullOrEmpty(_globalNamespace) ? (moduleName ?? clrType.FullName) : _globalNamespace + "." + clrType.Name;

            // remove e.g. `1 from the names of generic types
            var backTick = fullName.IndexOf('`');
            if (backTick > 0)
            {
                fullName = fullName.Remove(backTick);
            }

            return fullName;
        }

        public (Type type, bool nullable) UnwrapType(Type clrType)
        {
            if (clrType.IsGenericTask())
            {
                clrType = clrType.GetUnderlyingTaskType();
            }

            if (clrType.GetDnxCompatible().IsGenericType && clrType.GetGenericTypeDefinition().FullName == "Microsoft.AspNetCore.Mvc.ActionResult`1")
            {
                clrType = clrType.GetDnxCompatible().GetGenericArguments().Single();
            }

            bool nullable = false;
            if (clrType.IsNullable())
            {
                clrType = clrType.GetUnderlyingNullableType();
                nullable = true;
            }

            if (clrType.IsClass || clrType.IsInterface)
            {
                nullable = true;
            }

            return (clrType, nullable);
        }

        private (string type, bool isComposite) getTypeScriptName(Type clrType)
        {
            bool nullable;

            (clrType, nullable) = UnwrapType(clrType);

            if (_customConversions != null && _customConversions.TryGetValue(clrType, out var result))
            {
                return (result, true);
            }

            if (_cache.TryGetValue(clrType, out var result2))
            {
                return typeOrNull(result2.nullable, (result2.type, false));
            }

            if (clrType.Name == "IActionResult")
            {
                return ("any /* IActionResult */", false);
            }

            if (clrType.Name == "IFormCollection")
            {
                return ("FormData", false);
            }

            // these objects generally just mean json, so 'any' is appropriate.
            if (clrType.Namespace == "Newtonsoft.Json.Linq")
            {
                return ("any", false);
            }

            // Dictionaries -- these should come before IEnumerables, because they also implement IEnumerable
            if (clrType.IsIDictionary())
            {
                if (clrType.GenericTypeArguments.Length != 2)
                    return typeOrNull(true, ("{ [key: string]: any }", false));

                // TODO: can we use Map<> instead? this will break if the first argument is not string | number.
                return typeOrNull(true, ($"{{ [key: {getTypeScriptName(clrType.GetDnxCompatible().GetGenericArguments()[0]).type}]: {getTypeScriptName(clrType.GetDnxCompatible().GetGenericArguments()[1]).type} }}", false));
            }

            if (clrType.IsArray)
            {
                return typeOrNull(true, (wrapComposite(getTypeScriptName(clrType.GetElementType())) + "[]", false));
            }

            if (typeof(IEnumerable).GetDnxCompatible().IsAssignableFrom(clrType))
            {
                if (clrType.GetDnxCompatible().IsGenericType)
                {
                    return typeOrNull(true, (wrapComposite(getTypeScriptName(clrType.GetDnxCompatible().GetGenericArguments()[0])) + "[]", false));
                }
                return typeOrNull(true, ("any[]", false));
            }

            if (clrType.GetDnxCompatible().IsEnum)
            {
                _fluent.ModelBuilder.Add(clrType);
                return typeOrNull(nullable, (GetFullName(clrType), false));
            }

            if (clrType.Namespace == "System" || clrType.Namespace.StartsWith("System."))
                return ("any", false);

            if (clrType.GetDnxCompatible().IsClass || clrType.GetDnxCompatible().IsInterface)
            {
                _fluent.ModelBuilder.Add(clrType);

                var name = GetFullName(clrType);
                if (clrType.GetDnxCompatible().IsGenericType)
                {
                    name += "<";
                    var count = 0;
                    foreach (var genericArgument in clrType.GetDnxCompatible().GetGenericArguments())
                    {
                        if (count++ != 0) name += ", ";
                        name += getTypeScriptName(genericArgument).type;
                    }
                    name += ">";
                }
                return typeOrNull(nullable, (name, false));
            }

            Console.WriteLine("WARNING: Unknown conversion for type: " + GetFullName(clrType));
            return ("any", false);
        }

        private (string type, bool isComposite) typeOrNull(bool isNullable, (string type, bool isComposite) t)
        {
            if (!_strictNullChecks)
                return t;
            return isNullable ? (wrapComposite(t) + " | null", true) : t;
        }

        private string wrapComposite((string type, bool isComposite) p) => p.isComposite ? $"({p.type})" : p.type;

        public string GetTypeScriptName(Type clrType) => getTypeScriptName(clrType).type;

        public string PrettyClrTypeName(Type t)
        {
            if (!t.GetDnxCompatible().IsGenericType)
                return t.FullName;

            return t.Namespace + "." + string.Format(
                "{0}<{1}>",
                t.Name.Substring(0, t.Name.LastIndexOf("`", StringComparison.Ordinal)),
                string.Join(", ", t.GetDnxCompatible().GetGenericArguments().Select(PrettyClrTypeName)));
        }
    }
}
