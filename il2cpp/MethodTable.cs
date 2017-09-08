﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using dnlib.DotNet;

namespace il2cpp
{
	// 类型方法编组
	using TypeMethodPair = Tuple<MethodTable, MethodDef>;
	// 类型名称方法编组
	using TypeNameMethodPair = Tuple<string, MethodDef>;

	// 虚槽数据
	internal class VirtualSlot
	{
		public readonly HashSet<TypeMethodPair> Entries = new HashSet<TypeMethodPair>();
		public readonly TypeMethodPair NewSlotEntry;
		public TypeMethodPair Implemented;

		public VirtualSlot(VirtualSlot other, TypeMethodPair newEntry)
		{
			Entries.UnionWith(other.Entries);
			NewSlotEntry = other.NewSlotEntry;
			Implemented = other.Implemented;

			if (NewSlotEntry == null)
				NewSlotEntry = newEntry;
		}

		public VirtualSlot(TypeMethodPair newEntry)
		{
			NewSlotEntry = newEntry;
		}
	}

	// 冲突解决编组
	internal class ConflictPair
	{
		// 重用槽位的方法
		public readonly List<MethodDef> ReuseSlots = new List<MethodDef>();
		// 新建槽位的方法
		public readonly List<MethodDef> NewSlots = new List<MethodDef>();

		public bool ContainsConflicts()
		{
			return ReuseSlots.Count + NewSlots.Count > 1;
		}
	}

	// 展开的虚表
	internal class VirtualTable
	{
		// 入口实现映射
		private readonly Dictionary<TypeNameMethodPair, TypeNameMethodPair> EntryMap =
			new Dictionary<TypeNameMethodPair, TypeNameMethodPair>();
		// 方法替换映射
		private readonly Dictionary<MethodDef, TypeNameMethodPair> ReplaceMap =
			new Dictionary<MethodDef, TypeNameMethodPair>();
		// 同类内的方法替换映射
		private readonly Dictionary<MethodDef, TypeNameMethodPair> SameTypeReplaceMap =
			new Dictionary<MethodDef, TypeNameMethodPair>();

		private readonly Dictionary<TypeNameMethodPair, TypeNameMethodPair> CachedMap =
			new Dictionary<TypeNameMethodPair, TypeNameMethodPair>();

		public VirtualTable(MethodTable mtable)
		{
			foreach (var kv in mtable.EntryMap)
			{
				EntryMap.Add(
					new TypeNameMethodPair(kv.Key.Item1.GetNameKey(), kv.Key.Item2),
					new TypeNameMethodPair(kv.Value.Item1.GetNameKey(), kv.Value.Item2));
			}

			foreach (var kv in mtable.ReplaceMap)
			{
				ReplaceMap.Add(
					kv.Key,
					new TypeNameMethodPair(kv.Value.Item1.GetNameKey(), kv.Value.Item2));
			}

			foreach (var kv in mtable.SameTypeReplaceMap)
			{
				SameTypeReplaceMap.Add(
					kv.Key,
					new TypeNameMethodPair(kv.Value.Item1.GetNameKey(), kv.Value.Item2));
			}
		}

		public bool QueryCallReplace(
			MethodDef entryDef,
			out string implTypeName,
			out MethodDef implDef)
		{
			if (SameTypeReplaceMap.TryGetValue(entryDef, out var implPair))
			{
				implTypeName = implPair.Item1;
				implDef = implPair.Item2;
				return true;
			}
			implTypeName = null;
			implDef = null;
			return false;
		}

		public void QueryCallVirt(
			string entryTypeName,
			MethodDef entryDef,
			out string implTypeName,
			out MethodDef implDef)
		{
			var entryPair = new TypeNameMethodPair(entryTypeName, entryDef);
			if (!CachedMap.TryGetValue(entryPair, out var implPair))
			{
				QueryCallVirtImpl(entryPair, out implPair);
				CachedMap[entryPair] = implPair;
			}
			implTypeName = implPair.Item1;
			implDef = implPair.Item2;
		}

		private void QueryCallVirtImpl(
			TypeNameMethodPair entryPair,
			out TypeNameMethodPair implPair)
		{
			for (; ; )
			{
				if (EntryMap.TryGetValue(
					entryPair,
					out implPair))
				{
					if (!ReplaceMap.TryGetValue(implPair.Item2, out entryPair))
						return;
				}
				else
				{
					throw new TypeLoadException(
						string.Format("Virtual method can't resolve: {0} -> {1}",
							entryPair.Item1,
							entryPair.Item2));
				}
			}
		}
	}

	// 方法表
	internal class MethodTable
	{
		public readonly Il2cppContext Context;
		public readonly TypeDef Def;

		private string NameKey;
		private IList<TypeSig> InstGenArgs;

		// 槽位映射
		public readonly Dictionary<string, VirtualSlot> SlotMap = new Dictionary<string, VirtualSlot>();
		// 入口实现映射
		public readonly Dictionary<TypeMethodPair, TypeMethodPair> EntryMap = new Dictionary<TypeMethodPair, TypeMethodPair>();
		// 方法替换映射
		public readonly Dictionary<MethodDef, TypeMethodPair> ReplaceMap = new Dictionary<MethodDef, TypeMethodPair>();
		// 同类内的方法替换映射
		public readonly Dictionary<MethodDef, TypeMethodPair> SameTypeReplaceMap = new Dictionary<MethodDef, TypeMethodPair>();

		// 未实现的接口映射
		private Dictionary<string, HashSet<TypeMethodPair>> NotImplInterfaces;

		public MethodTable(Il2cppContext context, TypeDef tyDef)
		{
			Context = context;
			Def = tyDef;
		}

		public override string ToString()
		{
			return NameKey;
		}

		public string GetNameKey()
		{
			if (NameKey == null)
			{
				StringBuilder sb = new StringBuilder();
				Helper.TypeNameKey(sb, Def, InstGenArgs);
				NameKey = sb.ToString();
			}
			return NameKey;
		}

		public string GetExpandedNameKey(IList<TypeSig> genArgs)
		{
			Debug.Assert(genArgs.IsCollectionValid());
			Debug.Assert(!InstGenArgs.IsCollectionValid());
			Debug.Assert(Def.GenericParameters.Count == genArgs.Count);

			StringBuilder sb = new StringBuilder();
			Helper.TypeNameKey(sb, Def, genArgs);
			return sb.ToString();
		}

		public MethodTable ExpandTable(IList<TypeSig> genArgs)
		{
			Debug.Assert(genArgs.IsCollectionValid());
			Debug.Assert(!InstGenArgs.IsCollectionValid());
			Debug.Assert(Def.GenericParameters.Count == genArgs.Count);

			bool isInterface = Def.IsInterface;

			MethodTable expMetTable = new MethodTable(Context, Def);
			expMetTable.InstGenArgs = genArgs;
			expMetTable.GetNameKey();

			IGenericReplacer replacer = new TypeDefGenReplacer(Def, genArgs);
			StringBuilder sb = new StringBuilder();

			foreach (var kv in SlotMap)
			{
				Debug.Assert(kv.Value.Entries.Count != 0);

				VirtualSlot vslot = ExpandVirtualSlot(kv.Value, expMetTable, replacer);

				Helper.MethodDefNameKey(sb, vslot.Entries.First().Item2, replacer);
				string metNameKey = sb.ToString();
				sb.Clear();

				// 接口合并相同签名的方法
				if (isInterface)
					expMetTable.MergeInterfaces(metNameKey, vslot.Entries);
				else
					expMetTable.SlotMap[metNameKey] = vslot;
			}

			foreach (var kv in EntryMap)
			{
				expMetTable.EntryMap[ExpandMethodPair(kv.Key, expMetTable, replacer)] =
					ExpandMethodPair(kv.Value, expMetTable, replacer);
			}

			foreach (var kv in ReplaceMap)
			{
				expMetTable.ReplaceMap[kv.Key] = ExpandMethodPair(kv.Value, expMetTable, replacer);
			}

			foreach (var kv in SameTypeReplaceMap)
			{
				expMetTable.SameTypeReplaceMap[kv.Key] = ExpandMethodPair(kv.Value, expMetTable, replacer);
			}

			return expMetTable;
		}

		private TypeMethodPair ExpandMethodPair(TypeMethodPair metPair, MethodTable expMetTable, IGenericReplacer replacer)
		{
			if (metPair == null)
				return null;

			MethodTable mtable = metPair.Item1;
			if (mtable == this)
				return new TypeMethodPair(expMetTable, metPair.Item2);

			if (mtable.InstGenArgs.IsCollectionValid())
			{
				var genArgs = Helper.ReplaceGenericSigList(mtable.InstGenArgs, replacer);
				mtable = Context.TypeMgr.ResolveMethodTableSpec(mtable.Def, genArgs);

				Debug.Assert(mtable != this);
				return new TypeMethodPair(mtable, metPair.Item2);
			}

			return metPair;
		}

		private VirtualSlot ExpandVirtualSlot(VirtualSlot vslot, MethodTable expMetTable, IGenericReplacer replacer)
		{
			VirtualSlot expVSlot = new VirtualSlot(ExpandMethodPair(vslot.NewSlotEntry, expMetTable, replacer));

			foreach (var entry in vslot.Entries)
				expVSlot.Entries.Add(ExpandMethodPair(entry, expMetTable, replacer));

			expVSlot.Implemented = ExpandMethodPair(vslot.Implemented, expMetTable, replacer);

			return expVSlot;
		}

		public void ResolveTable()
		{
			Debug.Assert(InstGenArgs == null);

			var metDefList = new List<Tuple<string, MethodDef>>();
			var conflictMap = new Dictionary<string, ConflictPair>();

			bool isInterface = Def.IsInterface;

			StringBuilder sb = new StringBuilder();

			uint lastRid = 0;
			foreach (MethodDef metDef in Def.Methods)
			{
				// 跳过非虚方法
				if (!metDef.IsVirtual)
				{
					// 非虚方法如果存在显式重写则视为错误
					if (metDef.HasOverrides)
					{
						throw new TypeLoadException(
							string.Format("Explicit overridden method must be virtual: {0}",
								metDef.FullName));
					}

					continue;
				}

				Debug.Assert(lastRid == 0 || lastRid < metDef.Rid);
				lastRid = metDef.Rid;

				// 获得方法签名
				Helper.MethodDefNameKey(sb, metDef, null);
				string metNameKey = sb.ToString();
				sb.Clear();

				if (isInterface)
				{
					Debug.Assert(metDef.IsAbstract && metDef.IsNewSlot);
					metDefList.Add(new Tuple<string, MethodDef>(metNameKey, metDef));
				}
				else
				{
					// 特殊处理签名冲突的方法
					if (!conflictMap.TryGetValue(metNameKey, out var confPair))
					{
						confPair = new ConflictPair();
						conflictMap.Add(metNameKey, confPair);
						metDefList.Add(new Tuple<string, MethodDef>(metNameKey, metDef));
					}

					if (metDef.IsNewSlot)
						confPair.NewSlots.Add(metDef);
					else
						confPair.ReuseSlots.Add(metDef);
				}
			}

			if (!isInterface)
			{
				foreach (var item in conflictMap.Where(kvp => !kvp.Value.ContainsConflicts()).ToList())
				{
					conflictMap.Remove(item.Key);
				}
			}

			// 解析基类方法表
			MethodTable baseTable = null;
			if (Def.BaseType != null)
			{
				baseTable = Context.TypeMgr.ResolveMethodTable(Def.BaseType);

				// 继承基类数据
				DerivedTable(baseTable);
			}

			var expOverrides = new List<Tuple<string, MethodDef>>();
			// 解析隐式重写
			foreach (var metItem in metDefList)
			{
				string metNameKey = metItem.Item1;

				if (isInterface)
				{
					// 接口需要合并相同签名的方法
					MethodDef metDef = metItem.Item2;
					var entry = new TypeMethodPair(this, metDef);
					MergeInterface(metNameKey, entry);
				}
				else if (conflictMap.TryGetValue(metNameKey, out var confPair))
				{
					Debug.Assert(confPair.ContainsConflicts());

					// 冲突签名的方法需要先处理重写槽方法, 再处理新建槽方法
					VirtualSlot lastVSlot = null;
					foreach (var metDef in confPair.ReuseSlots)
						lastVSlot = ProcessMethod(metNameKey, metDef, baseTable, expOverrides);

					// 应用重写信息到入口映射
					ApplyVirtualSlot(lastVSlot);

					foreach (var metDef in confPair.NewSlots)
						ProcessMethod(metNameKey, metDef, baseTable, expOverrides);
				}
				else
				{
					MethodDef metDef = metItem.Item2;
					ProcessMethod(metNameKey, metDef, baseTable, expOverrides);
				}
			}
			metDefList = null;
			conflictMap = null;
			baseTable = null;

			// 记录显式重写目标以便查重
			var expOverTargets = new HashSet<TypeMethodPair>();

			// 解析显式重写
			foreach (var expItem in expOverrides)
			{
				string metNameKey = expItem.Item1;
				MethodDef metDef = expItem.Item2;

				foreach (MethodOverride metOver in metDef.Overrides)
				{
					var overTarget = metOver.MethodDeclaration;
					var overImpl = metOver.MethodBody;

					MethodTable targetTable = Context.TypeMgr.ResolveMethodTable(overTarget.DeclaringType);
					MethodDef targetDef = overTarget.ResolveMethodDef();

					// 验证显式重写目标的正确性
					if (targetDef == null || targetDef.DeclaringType != targetTable.Def)
					{
						throw new TypeLoadException(
							string.Format("Illegal explicit overriding target: {0}",
								overTarget.FullName));
					}

					var targetEntry = new TypeMethodPair(targetTable, targetDef);

					MethodDef implDef = overImpl.ResolveMethodDef();
					Debug.Assert(metDef == implDef);

					// 同一个类内重复的显式重写, 以及重写存在重写的方法, 视为错误
					if ((targetTable == this && targetDef.HasOverrides) ||
						expOverTargets.Contains(targetEntry))
					{
						throw new TypeLoadException(
							string.Format("Explicit overriding target has been overridden: {0}",
								overTarget.FullName));
					}
					expOverTargets.Add(targetEntry);

					if (targetTable.Def.IsInterface)
					{
						// 接口方法显式重写
						RemoveSlotEntry(targetEntry);
						var vslot = SlotMap[metNameKey];
						vslot.Entries.Add(targetEntry);
						ApplyVirtualSlot(vslot);
					}
					else
					{
						// 类方法显式重写
						var impl = new TypeMethodPair(this, implDef);

						// 相同类型的需要单独添加, 以便非虚调用时处理替换
						if (targetTable == this)
							SameTypeReplaceMap[targetDef] = impl;

						// 如果当前方法存在新建槽位的那个方法, 则显式重写记录使用新建槽方法
						var newSlotEntry = SlotMap[metNameKey].NewSlotEntry;
						if (newSlotEntry != null)
							ReplaceMap[targetDef] = newSlotEntry;
						else
							ReplaceMap[targetDef] = impl;
					}
				}
			}
			expOverTargets = null;

			// 关联抽象基类未实现的接口
			if (NotImplInterfaces.IsCollectionValid())
			{
				List<string> removedKeys = new List<string>();
				foreach (var kv in NotImplInterfaces)
				{
					string metNameKey = kv.Key;
					var notImplEntries = kv.Value;
					if (SlotMap.TryGetValue(metNameKey, out var vslot))
					{
						vslot.Entries.UnionWith(notImplEntries);
						removedKeys.Add(metNameKey);
					}
				}
				foreach (var key in removedKeys)
					NotImplInterfaces.Remove(key);
			}

			// 关联接口
			if (Def.HasInterfaces)
			{
				foreach (var inf in Def.Interfaces)
				{
					MethodTable infTable = Context.TypeMgr.ResolveMethodTable(inf.Interface);
					foreach (var kv in infTable.SlotMap)
					{
						string metNameKey = kv.Key;
						var infEntries = kv.Value.Entries;

						if (isInterface)
						{
							MergeInterfaces(metNameKey, infEntries);
						}
						else if (SlotMap.TryGetValue(metNameKey, out var vslot) &&
								 vslot.Implemented.Item1 == this)
						{
							// 关联当前类型内签名相同的方法
							vslot.Entries.UnionWith(infEntries);
						}
						else
						{
							foreach (var entry in infEntries)
							{
								if (!EntryMap.ContainsKey(entry))
								{
									if (SlotMap.TryGetValue(metNameKey, out vslot))
									{
										// 关联继承链上签名相同的方法
										vslot.Entries.Add(entry);
									}
									else if (Def.IsAbstract)
									{
										AddNotImplInterface(metNameKey, entry);
									}
									else
									{
										throw new TypeLoadException(
												string.Format("Interface method not implemented in type {0}: {1} -> {2}",
													GetNameKey(),
													entry.Item1.GetNameKey(),
													entry.Item2));
									}
								}
							}
						}
					}
				}
			}

			if (isInterface)
				return;

			for (; ; )
			{
				// 展开入口映射
				foreach (var kv in SlotMap)
				{
					TypeMethodPair impl = kv.Value.Implemented;
					var entries = kv.Value.Entries;

					foreach (TypeMethodPair entry in entries)
					{
						EntryMap[entry] = impl;
					}
				}

				bool isRebound = false;
				// 对于非抽象类需要检查是否存在实现
				if (!Def.IsInterface && !Def.IsAbstract)
				{
					foreach (var kv in EntryMap)
					{
						if (kv.Value == null)
						{
							/*Helper.MethodDefNameKey(sb, kv.Key.Item2, null);
							string metNameKey = sb.ToString();
							sb.Clear();

							if (SlotMap.TryGetValue(metNameKey, out var vslot))
							{
								if (vslot.Entries.Add(kv.Key))
									isRebound = true;
							}
							else*/
							{
								throw new TypeLoadException(
									string.Format("Abstract method not implemented in type {0}: {1} -> {2}",
										GetNameKey(),
										kv.Key.Item1.GetNameKey(),
										kv.Key.Item2));
							}
						}
					}
				}
				if (!isRebound)
					break;
			}
		}

		private void DerivedTable(MethodTable baseTable)
		{
			foreach (var kv in baseTable.SlotMap)
				SlotMap.Add(kv.Key, new VirtualSlot(kv.Value, null));

			foreach (var kv in baseTable.EntryMap)
				EntryMap.Add(kv.Key, kv.Value);

			foreach (var kv in baseTable.ReplaceMap)
				ReplaceMap.Add(kv.Key, kv.Value);

			foreach (var kv in baseTable.SameTypeReplaceMap)
				SameTypeReplaceMap.Add(kv.Key, kv.Value);

			if (baseTable.NotImplInterfaces.IsCollectionValid())
			{
				Debug.Assert(baseTable.Def.IsAbstract);
				NotImplInterfaces = new Dictionary<string, HashSet<TypeMethodPair>>();
				foreach (var kv in baseTable.NotImplInterfaces)
					NotImplInterfaces.Add(kv.Key, new HashSet<TypeMethodPair>(kv.Value));
			}
		}

		private void MergeInterface(string metNameKey, TypeMethodPair entry)
		{
			Debug.Assert(entry.Item1.Def.IsInterface);
			if (!SlotMap.TryGetValue(metNameKey, out var vslot))
			{
				vslot = new VirtualSlot(null);
				SlotMap.Add(metNameKey, vslot);
			}
			vslot.Entries.Add(entry);
		}

		private void MergeInterfaces(string metNameKey, HashSet<TypeMethodPair> entrySet)
		{
			if (!SlotMap.TryGetValue(metNameKey, out var vslot))
			{
				vslot = new VirtualSlot(null);
				SlotMap.Add(metNameKey, vslot);
			}
			vslot.Entries.UnionWith(entrySet);
		}

		private VirtualSlot ProcessMethod(
			string metNameKey,
			MethodDef metDef,
			MethodTable baseTable,
			List<Tuple<string, MethodDef>> expOverrides)
		{
			Debug.Assert(metDef.IsVirtual);

			// 记录显式重写方法
			if (metDef.HasOverrides)
				expOverrides.Add(new Tuple<string, MethodDef>(metNameKey, metDef));

			var entry = new TypeMethodPair(this, metDef);
			var impl = Def.IsInterface ? null : entry;

			VirtualSlot vslot;
			if (metDef.IsReuseSlot)
			{
				// 对于重写槽方法, 如果不存在可重写的槽则转换为新建槽方法
				if (baseTable == null)
					metDef.IsNewSlot = true;
				else if (!baseTable.SlotMap.TryGetValue(metNameKey, out vslot))
					metDef.IsNewSlot = true;
				else
				{
					vslot = new VirtualSlot(vslot, impl);
					vslot.Entries.Add(entry);
					vslot.Implemented = impl;
					SlotMap[metNameKey] = vslot;
					return vslot;
				}
			}

			Debug.Assert(metDef.IsNewSlot);
			vslot = new VirtualSlot(impl);
			vslot.Entries.Add(entry);
			vslot.Implemented = impl;
			SlotMap[metNameKey] = vslot;
			return vslot;
		}

		private void ApplyVirtualSlot(VirtualSlot vslot)
		{
			Debug.Assert(vslot != null);

			var impl = vslot.Implemented;
			foreach (TypeMethodPair entry in vslot.Entries)
			{
				EntryMap[entry] = impl;
			}
		}

		private void RemoveSlotEntry(TypeMethodPair entry)
		{
			List<string> removedKeys = new List<string>();
			foreach (var kv in SlotMap)
			{
				string metNameKey = kv.Key;
				var vslot = kv.Value;

				vslot.Entries.Remove(entry);

				if (vslot.Entries.Count == 0)
				{
					Debug.Assert(vslot.Implemented == null);
					removedKeys.Add(metNameKey);
				}
			}
			foreach (var key in removedKeys)
				SlotMap.Remove(key);

			if (NotImplInterfaces.IsCollectionValid())
			{
				removedKeys.Clear();
				foreach (var kv in NotImplInterfaces)
				{
					string metNameKey = kv.Key;
					var notImplEntries = kv.Value;

					notImplEntries.Remove(entry);

					if (notImplEntries.Count == 0)
						removedKeys.Add(metNameKey);
				}
				foreach (var key in removedKeys)
					NotImplInterfaces.Remove(key);
			}
		}

		private void AddNotImplInterface(string metNameKey, TypeMethodPair notImplPair)
		{
			if (NotImplInterfaces == null)
				NotImplInterfaces = new Dictionary<string, HashSet<TypeMethodPair>>();

			if (!NotImplInterfaces.TryGetValue(metNameKey, out var entries))
			{
				entries = new HashSet<TypeMethodPair>();
				NotImplInterfaces.Add(metNameKey, entries);
			}
			entries.Add(notImplPair);
		}
	}
}
