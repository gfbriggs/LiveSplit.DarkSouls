﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveSplit.DarkSouls
{
	public static class Utilities
	{
		// This function is used to strip the category off raw item flags. Felt most reasonable to include it in this
		// utility class.
		public static int StripHighestDigit(int rawId, out int digit)
		{
			digit = rawId;

			int divisor = 1;

			// See https://stackoverflow.com/a/701355/7281613.
			while (digit > 10)
			{
				digit /= 10;
				divisor *= 10;
			}
			
			return rawId % divisor;
		}

		public static int ToHex(int value)
		{
			string hex = value.ToString("X");

			return int.Parse(hex[0].ToString());
		}
	}
}