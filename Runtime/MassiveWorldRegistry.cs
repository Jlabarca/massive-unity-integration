using System;
using System.Collections.Generic;

namespace Massive.Unity
{
	/// <summary>
	/// Simple runtime registry for manually-created Massive worlds.
	/// Call Register/Unregister so the State Inspector can discover non-static worlds.
	/// </summary>
	public static class MassiveWorldRegistry
	{
		private static readonly List<string> s_names = new List<string>();
		private static readonly List<World> s_worlds = new List<World>();

		public static int Count => s_names.Count;

		public static void Register(string name, World world)
		{
			if (world == null) throw new ArgumentNullException(nameof(world));
			if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name cannot be empty.", nameof(name));

			// Avoid duplicates
			for (int i = 0; i < s_names.Count; i++)
			{
				if (s_names[i] == name)
				{
					s_worlds[i] = world;
					return;
				}
			}

			s_names.Add(name);
			s_worlds.Add(world);
		}

		public static void Unregister(string name)
		{
			for (int i = 0; i < s_names.Count; i++)
			{
				if (s_names[i] == name)
				{
					s_names.RemoveAt(i);
					s_worlds.RemoveAt(i);
					return;
				}
			}
		}

		public static void Clear()
		{
			s_names.Clear();
			s_worlds.Clear();
		}

		public static string GetName(int index) => s_names[index];
		public static World GetWorld(int index) => s_worlds[index];

		public static bool TryGetWorldByName(string name, out World world)
		{
			for (int i = 0; i < s_names.Count; i++)
			{
				if (s_names[i] == name)
				{
					world = s_worlds[i];
					return true;
				}
			}
			world = null;
			return false;
		}
	}
}
