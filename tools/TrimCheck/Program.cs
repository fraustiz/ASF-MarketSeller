// Lists every type and member the plugin uses that doesn't exist in a (trimmed) ASF publish folder
using Mono.Cecil;

string pluginPath = args[0];
string runtimeDirectory = args[1];

DefaultAssemblyResolver resolver = new();

foreach (string directory in resolver.GetSearchDirectories()) {
	resolver.RemoveSearchDirectory(directory);
}

resolver.AddSearchDirectory(runtimeDirectory);

ModuleDefinition module = ModuleDefinition.ReadModule(pluginPath, new ReaderParameters { AssemblyResolver = resolver });
SortedSet<string> missing = new(StringComparer.Ordinal);

void Check(string kind, string name, Func<object?> resolve) {
	try {
		if (resolve() == null) {
			missing.Add($"{kind} {name}");
		}
	} catch (AssemblyResolutionException e) {
		missing.Add($"ASSEMBLY {e.AssemblyReference.FullName} (needed by {name})");
	}
}

foreach (TypeReference type in module.GetTypeReferences()) {
	Check("TYPE", type.FullName, type.Resolve);
}

foreach (MemberReference member in module.GetMemberReferences()) {
	switch (member) {
		case MethodReference method:
			Check("METHOD", method.FullName, method.Resolve);

			break;
		case FieldReference field:
			Check("FIELD", field.FullName, field.Resolve);

			break;
	}
}

// typeof() in attribute arguments, e.g. [JsonConverter(typeof(JsonStringEnumConverter<T>))], isn't in the reference tables
void CheckAttributes(ICustomAttributeProvider provider) {
	foreach (CustomAttribute attribute in provider.CustomAttributes) {
		foreach (CustomAttributeArgument argument in attribute.ConstructorArguments.Concat(attribute.Properties.Select(static p => p.Argument)).Concat(attribute.Fields.Select(static f => f.Argument))) {
			if (argument.Value is TypeReference typeArgument) {
				Check("ATTRIBUTE TYPE", typeArgument.FullName, typeArgument.Resolve);

				if (typeArgument is GenericInstanceType generic) {
					Check("ATTRIBUTE TYPE", generic.ElementType.FullName, generic.ElementType.Resolve);
				}
			}
		}
	}
}

CheckAttributes(module.Assembly);
CheckAttributes(module);

foreach (TypeDefinition type in module.GetTypes()) {
	CheckAttributes(type);

	foreach (IMemberDefinition member in type.Methods.Cast<IMemberDefinition>().Concat(type.Properties).Concat(type.Fields).Concat(type.Events)) {
		CheckAttributes(member);
	}
}

foreach (string line in missing) {
	Console.WriteLine(line);
}

Console.WriteLine($"{missing.Count} missing");

return missing.Count == 0 ? 0 : 1;
