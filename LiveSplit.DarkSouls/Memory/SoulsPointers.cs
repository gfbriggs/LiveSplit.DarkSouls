﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveSplit.DarkSouls.Memory
{
	public class SoulsPointers
	{
		// This array is used to reset equipment indexes.
		private static byte?[] equipmentBytes =
		{
			0x8B, 0x4C, 0x24, 0x34, 0x8B, 0x44, 0x24, 0x2C, 0x89, 0x8A, 0x38, 0x01, 0x00, 0x00, 0x8B, 0x90, 0x08, 0x01,
			0x00, 0x00, 0xC1, 0xE2, 0x10, 0x0B, 0x90, 0x00, 0x01, 0x00, 0x00, 0x8B, 0xC1, 0x8B, 0xCD, 0x89, 0x14, 0xAD,
			null, null, null, null
		};

		private IntPtr handle;

		public SoulsPointers(Process process)
		{
			handle = process.Handle;

			// Unlike other pointers, the equipment pointer (used to reset equipment indexes on timer reset) is only
			// scanned once when the process is hooked.
			Equipment = MemoryScanner.Scan(process, equipmentBytes, 0x24);
			Refresh(process);
		}

		public IntPtr Character { get; private set; }
		public IntPtr CharacterStats { get; private set; }
		public IntPtr CharacterMap { get; private set; }
		public IntPtr CharacterPosition { get; private set; }
		public IntPtr Equipment { get; }
		public IntPtr Inventory { get; private set; }
		public IntPtr WorldState { get; private set; }
		public IntPtr Zone { get; private set; }

		public void Refresh(Process process)
		{
			IntPtr character = (IntPtr)MemoryTools.ReadInt(handle, (IntPtr)0x137DC70);
			character = (IntPtr)MemoryTools.ReadInt(handle, character + 0x4);
			character = (IntPtr)MemoryTools.ReadInt(handle, character);
			Character = character;

			IntPtr characterStats = (IntPtr)MemoryTools.ReadInt(handle, (IntPtr)0x1378700);
			characterStats = (IntPtr)MemoryTools.ReadInt(handle, characterStats + 0x8);
			CharacterStats = characterStats;

			CharacterMap = (IntPtr)MemoryTools.ReadInt(handle, character + 0x28);
			CharacterPosition = (IntPtr)MemoryTools.ReadInt(handle, CharacterMap + 0x1C);

			IntPtr inventory = (IntPtr)MemoryTools.ReadInt(handle, (IntPtr)0x1378700);
			inventory = (IntPtr)MemoryTools.ReadInt(handle, inventory + 0x8);
			Inventory = inventory + 0x1B8;

			WorldState = (IntPtr)MemoryTools.ReadInt(handle, (IntPtr)0x13784A0);
			Zone = (IntPtr)MemoryTools.ReadInt(handle, (IntPtr)0x137E204);
		}
	}
}