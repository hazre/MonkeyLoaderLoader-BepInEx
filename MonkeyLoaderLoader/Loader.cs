using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace MonkeyLoaderLoader.BepInEx;

class MonkeyLoaderPatch
{
	// Makes MonkeyLoader check the AppDomain for already loaded assemblies

	public static bool Prefix(string assemblyPath, ref Assembly __result)
	{
		var name = Path.GetFileNameWithoutExtension(assemblyPath);

		var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.Location == assemblyPath || a.GetName().Name == name);
		if (asm != null)
		{
			__result = asm;
			return false;
		}
		return true;
	}
}

class MonkeyLoaderWrapperPatch
{
	public class ExplodeException : Exception
	{
		public ExplodeException(string msg) : base(msg) { }
	}

	private static readonly MethodInfo _invokeMethod = AccessTools.Method(typeof(MethodBase), nameof(MethodBase.Invoke), [typeof(object), typeof(object[])]);

	private static readonly MethodInfo _loadFromAssemblyPathMethodOriginal = AccessTools.Method(typeof(AssemblyLoadContext), nameof(AssemblyLoadContext.LoadFromAssemblyPath));

	private static readonly MethodInfo _loadFromAssemblyPathMethodReplacement = AccessTools.Method(typeof(MonkeyLoaderWrapperPatch), nameof(LoadFromAssemblyPath));

	private static readonly MethodInfo _explodeMethod = AccessTools.Method(typeof(MonkeyLoaderWrapperPatch), nameof(Explode));

	private static Assembly LoadFromAssemblyPath(AssemblyLoadContext alc, string assemblyPath)
	{
		var name = Path.GetFileNameWithoutExtension(assemblyPath);
		var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.Location == assemblyPath || a.GetName().Name == name);
		if (asm != null)
		{
			return asm;
		}
		return alc.LoadFromAssemblyPath(assemblyPath); // should never happen if everything is preloaded
	}

	private static void Explode()
	{
		throw new ExplodeException("I couldn't figure out a better way to stop MonkeyLoaderWrapper from starting Resonite...");
	}

	public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		var instArr = instructions.ToArray();
		for (var i = 0; i < instArr.Length; i++)
		{
			var instruction = instArr[i];

			if (instruction.Calls(_invokeMethod) && instArr[i-1].opcode == OpCodes.Ldnull)
			{
				yield return instruction;
				yield return new CodeInstruction(OpCodes.Call, _explodeMethod);
			}
			else if (instruction.Calls(_loadFromAssemblyPathMethodOriginal))
			{
				yield return new CodeInstruction(OpCodes.Call, _loadFromAssemblyPathMethodReplacement);
			}
			else
			{
				yield return instruction;
			}
		}
	}
}

class MonkeyLoaderLoader
{
	private static readonly FileInfo _monkeyLoaderWrapperPath = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? new("MonkeyLoaderWrapper.Linux.dll") : new("MonkeyLoaderWrapper.dll");
	private static Assembly? _monkeyLoaderWrapperAsm;
	
	private static void PreloadAssemblies()
	{
		_monkeyLoaderWrapperAsm = Assembly.LoadFrom(_monkeyLoaderWrapperPath.FullName);
		var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
		foreach (var file in Directory.GetFiles("MonkeyLoader").Where(f => f.EndsWith(".dll")))
		{
			var name = Path.GetFileNameWithoutExtension(file);
			if (loadedAssemblies.Any(a => a.GetName().Name == name)) continue;
			Plugin.Log!.LogDebug($"Preloading: {name}");
			Assembly.LoadFrom(file);
		}

		foreach (var alc in AssemblyLoadContext.All)
		{
			alc.Resolving += (context, assemblyName) =>
				AppDomain.CurrentDomain.GetAssemblies()
					.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
		}
	}

	public static void Load(Harmony harmony)
	{
		try
		{
			PreloadAssemblies();
		}
		catch (FileNotFoundException e)
		{
			// This just means MonkeyLoader is probably not installed. So just print a small little error message instead of a massive stack trace.
			Plugin.Log!.LogError($"Could not find a required MonkeyLoader file!\nThis means MonkeyLoader will not be loaded!\nMissing file: {e.FileName}");
			return;
		}
		catch (Exception)
		{
			Plugin.Log!.LogError($"Error occurred when preloading assemblies.");
			throw;
		}

		try
		{
			var targetType = _monkeyLoaderWrapperAsm!.EntryPoint!.DeclaringType;
			var targetMethod = AccessTools.GetDeclaredMethods(targetType).FirstOrDefault(m => m.ReturnType == typeof(Task) && m.Name == "Main");
			harmony.Patch(AccessTools.AsyncMoveNext(targetMethod), transpiler: new(MonkeyLoaderWrapperPatch.Transpiler));

			var loadStrategyType = AccessTools.TypeByName("MonkeyLoader.AssemblyLoadContextLoadStrategy");
			var targetMethod2 = AccessTools.Method(loadStrategyType, "LoadFile");
			harmony.Patch(targetMethod2, prefix: new(MonkeyLoaderPatch.Prefix));
		}
		catch (Exception)
		{
			Plugin.Log!.LogError($"Error occurred when patching.");
			throw;
		}

		try
		{
			_monkeyLoaderWrapperAsm!.EntryPoint!.Invoke(null, [null]);
		}
		catch (TargetInvocationException e)
		{
			if (e.InnerException is not MonkeyLoaderWrapperPatch.ExplodeException) // ExplodeException means it worked
			{
				Plugin.Log!.LogError($"Error occurred in MonkeyLoader code.");
				throw;
			}
		}
		catch (Exception)
		{
			Plugin.Log!.LogError($"Error occurred in MonkeyLoader code.");
			throw;
		}

		Plugin.Log!.LogInfo("Done!");
	}
}
