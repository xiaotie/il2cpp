﻿using System;
using System.Text;
using dnlib.DotNet;

namespace il2cpp
{
	internal class GeneratorContext
	{
		private readonly TypeManager TypeMgr;

		public GeneratorContext(TypeManager typeMgr)
		{
			TypeMgr = typeMgr;
		}

		public string GetTypeName(TypeSig tySig)
		{
			switch (tySig.ElementType)
			{
				case ElementType.I1:
					return "int8_t";
				case ElementType.I2:
					return "int16_t";
				case ElementType.I4:
					return "int32_t";
				case ElementType.I8:
					return "int64_t";
				case ElementType.U1:
					return "uint8_t";
				case ElementType.U2:
					return "uint16_t";
				case ElementType.U4:
					return "uint32_t";
				case ElementType.U8:
					return "uint64_t";
				case ElementType.Boolean:
					return "uint8_t";
				case ElementType.Char:
					return "uint16_t";
				case ElementType.R4:
					return "float";
				case ElementType.R8:
					return "double";
				case ElementType.I:
					return "intptr_t";
				case ElementType.U:
					return "uintptr_t";

				case ElementType.Ptr:
				case ElementType.ByRef:
					return GetTypeName(tySig.Next) + '*';

				case ElementType.Object:
					return "cls_Object*";

				case ElementType.Class:
				case ElementType.ValueType:
				case ElementType.GenericInst:
					return GetTypeName(FindType(tySig)) + (tySig.IsValueType ? null : "*");

				default:
					throw new NotImplementedException();
			}

			return null;
		}

		public string GetTypeName(TypeX tyX)
		{
			string strName = tyX.GenTypeName;
			if (strName != null)
				return strName;

			strName = tyX.Def.IsValueType ? "stru_" : "cls_";

			string nameKey = tyX.GetNameKey();
			if (tyX.Def.DefinitionAssembly.IsCorLib())
				strName += EscapeName(nameKey);
			else
			{
				StringBuilder sb = new StringBuilder();
				sb.Append(tyX.Def.Name);
				var genArgs = tyX.GenArgs;
				if (genArgs.IsCollectionValid())
				{
					sb.Append('_');
					Helper.TypeSigListName(sb, genArgs, false);
				};
				strName += NameHash(nameKey.GetHashCode()) + '_' + EscapeName(sb.ToString());
			}

			tyX.GenTypeName = strName;
			return strName;
		}

		private TypeX FindType(TypeSig tySig)
		{
			StringBuilder sb = new StringBuilder();
			Helper.TypeSigName(sb, tySig, false);
			return TypeMgr.GetTypeByName(sb.ToString());
		}

		public string GetMethodName(MethodX metX, string prefix)
		{
			string strName = metX.GenMethodName;
			if (strName == null)
			{
				int hashCode = metX.GetNameKey().GetHashCode() ^ metX.DeclType.GetNameKey().GetHashCode();
				strName = NameHash(hashCode) + '_' + EscapeName(metX.DeclType.Def.Name) + "__" + EscapeName(metX.Def.Name);
				metX.GenMethodName = strName;
			}
			return prefix + strName;
		}

		private static string EscapeName(string fullName)
		{
			StringBuilder sb = new StringBuilder();

			for (int i = 0; i < fullName.Length; ++i)
			{
				char ch = fullName[i];
				if (IsLegalIdentChar(ch))
					sb.Append(ch);
				else if (ch >= 0x7F)
					sb.AppendFormat("{0:X}", ch);
				else
					sb.Append('_');
			}
			return sb.ToString();
		}

		private static bool IsLegalIdentChar(char ch)
		{
			return ch >= 'a' && ch <= 'z' ||
				   ch >= 'A' && ch <= 'Z' ||
				   ch >= '0' && ch <= '9' ||
				   ch == '_';
		}

		private static string NameHash(int hashCode)
		{
			return ToRadix((uint)hashCode, (uint)DigMap.Length);
		}

		private const string DigMap = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
		private static string ToRadix(uint value, uint radix)
		{
			StringBuilder sb = new StringBuilder();
			do
			{
				uint dig = value % radix;
				value /= radix;
				sb.Append(DigMap[(int)dig]);
			} while (value != 0);

			return sb.ToString();
		}
	}
}