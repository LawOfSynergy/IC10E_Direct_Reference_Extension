using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using BepInEx;
using IC10_Extender;
using static IC10_Extender.HelpString;
using System.ComponentModel;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Net;
using Assets.Scripts;
using BepInEx.Logging;
using System.Reflection;

namespace IC10E__Direct_Reference_Extension
{
    [BepInPlugin("net.lawofsynergy.stationeers.ic10e.dre", "[IC10E] Direct Reference Extensions", "0.0.4.0")]
    [BepInDependency("net.lawofsynergy.stationeers.ic10e")]
    public class Plugin : BaseUnityPlugin
    {
        public static new ManualLogSource Logger;

        void Awake() {
            Logger = base.Logger;
            IC10Extender.Register(new LoadSlotByDirectReference());
            IC10Extender.Register(new SetSlotByDirectReference());
            IC10Extender.Register(new PutDevicesInBatch());
            IC10Extender.Register(new PutDevicesInBatchWithName());
            IC10Extender.Register(new PutDirectlyDevicesInBatch());
            IC10Extender.Register(new PutDirectlyDevicesInBatchWithName());
            IC10Extender.Register(new PutDevicesGroupedByName());
            IC10Extender.Register(new PutDirectlyDevicesGroupedByName());
            IC10Extender.Register(new ClearRange());
            IC10Extender.Register(new ClearDirectlyRange());
        }
    }

    public class Constants
    {
        public static readonly HelpString R_INT = REGISTER + INTEGER;
        public static readonly HelpString ID = R_INT.Var("id");
        public static readonly HelpString DEVICE_HASH = R_INT.Var("deviceHash");
        public static readonly HelpString NAME_HASH = R_INT.Var("nameHash");
        public static readonly HelpString ADDRESS = R_INT.Var("startAddress");
        public static readonly HelpString COUNT = R_INT.Var("count");
    }

    public static class Utils
    {
        public static void LoadToStack(IMemoryWritable writeable, IEnumerable<long> refIds, int start)
        {
            var count = refIds.Count();

            writeable.WriteMemory(start, count);
            var i = start + 1;
            foreach (var device in refIds)
            {
                writeable.WriteMemory(i, device);
                i++;
            }
        }

        public static void LoadToStack(IMemoryWritable writeable, IEnumerable<long[]> groupings, int groupingSize, int start)
        {
            var count = groupings.Count();

            writeable.WriteMemory(start, count);
            var i = start + 1;
            foreach (var grouping in groupings)
            {
                foreach (var device in grouping)
                {
                    writeable.WriteMemory(i, device);
                    i++;
                }
            }
        }

        public static void LoadToStack(IMemoryWritable writeable, IEnumerable<ILogicable> devices, int[] groupingDef, int start)
        {
            var groupings = devices.GroupBy(device => device.GetNameHash())
                .Where(grouping =>
                {
                    if (grouping.Count() < groupingDef.Length) return false;
                    foreach(var hash in groupingDef)
                    {
                        var count = grouping.Where(device => device.GetPrefabHash() == hash).Count();
                        if(count != 1) return false;
                    }
                    return true;

                })
                .Select(grouping =>
                {
                    long[] group = new long[groupingDef.Length];
                    for (int i = 0; i < groupingDef.Length; i++)
                    {
                        group[i] = grouping.Where(device => device.GetPrefabHash() == groupingDef[i]).First().ReferenceId;
                    }
                    return group;
                }
                );
            LoadToStack(writeable, groupings, groupingDef.Length, start);
        }
    }

    //utility base classes

    public abstract class ClearRangeOperation : Operation
    {
        protected readonly IntValuedVariable StartAddress;
        protected readonly IntValuedVariable Count;
        protected ClearRangeOperation(ChipWrapper chip, int lineNumber, string startAddress, string count) : base(chip, lineNumber)
        {
            StartAddress = new IntValuedVariable(chip.chip, lineNumber, startAddress, InstructionInclude.MaskIntValue, false);
            Count = new IntValuedVariable(chip.chip, lineNumber, count, InstructionInclude.MaskIntValue, false);
        }

        protected void Load(out int startAddress, out int count)
        {
            startAddress = StartAddress.GetVariableValue(AliasTarget.Register);
            count = Count.GetVariableValue(AliasTarget.Register);
        }

        protected abstract IMemoryWritable LoadWriteable();

        public override int Execute(int index)
        {
            try
            {
                Load(out var startAddress, out var count);
                var writeable = LoadWriteable();

                for (int i = startAddress; i < startAddress + count; i++)
                {
                    writeable.WriteMemory(i, 0);
                }
            }
            catch (Exception ex)
            {
                switch (ex)
                {
                    case ProgrammableChipException _: throw ex;
                    case StackUnderflowException _: throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.StackUnderFlow, LineNumber);
                    case StackOverflowException _: throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.StackOverFlow, LineNumber);
                    case NullReferenceException _: throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceNotFound, LineNumber);
                    default: throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.Unknown, LineNumber);
                }
            }

            return index + 1;
        }
    }

    public abstract class BatchStackOperation : Operation
    {
        protected readonly IntValuedVariable StartAddress;
        protected BatchStackOperation(ChipWrapper chip, int lineNumber, string address) : base(chip, lineNumber)
        {
            StartAddress = new IntValuedVariable(chip.chip, lineNumber, address, InstructionInclude.MaskIntValue, false);
        }

        protected void Load(out int startAddress)
        {
            startAddress = StartAddress.GetVariableValue(AliasTarget.Register);
        }

        protected IMemoryWritable LoadWriteable(int deviceIndex, int networkIndex)
        {
            var target = Chip.CircuitHousing.GetLogicableFromIndex(deviceIndex, networkIndex);
            if (target == null) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceNotFound, LineNumber);
            if (!(target is IMemoryWritable writeable)) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.MemoryNotWriteable, LineNumber);
            return writeable;
        }

        protected IMemoryWritable LoadWriteable(int refId)
        {
            var target = Chip.CircuitHousing.GetLogicableFromId(refId);
            if (target == null) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceNotFound, LineNumber);
            if (!(target is IMemoryWritable writeable)) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.MemoryNotWriteable, LineNumber);
            return writeable;
        }

        public override int Execute(int index)
        {
            try
            {
                return TryExecute(index);
            }
            catch (Exception ex)
            {
                switch (ex)
                {
                    case ProgrammableChipException _: throw ex;
                    case StackUnderflowException _: throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.StackUnderFlow, LineNumber);
                    case StackOverflowException _: throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.StackOverFlow, LineNumber);
                    case NullReferenceException _: throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceNotFound, LineNumber);
                    default:
                        Plugin.Logger.LogError(ex);
                        throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.Unknown, LineNumber);
                }
            }
        }

        protected abstract int TryExecute(int index);
    }

    public abstract class DeviceBatchStackOperation : BatchStackOperation
    {
        protected readonly DeviceIndexVariable DeviceIndex;
        protected DeviceBatchStackOperation(ChipWrapper chip, int lineNumber, string deviceIndex, string startAddress) : base(chip, lineNumber, startAddress)
        {
            DeviceIndex = new DeviceIndexVariable(chip.chip, lineNumber, deviceIndex, InstructionInclude.MaskDeviceIndex, false);
        }

        protected void Load(out int deviceIndex, out int networkIndex, out int startAddress)
        {
            Load(out startAddress);
            deviceIndex = DeviceIndex.GetVariableIndex(AliasTarget.Device);
            networkIndex = DeviceIndex.GetNetworkIndex();
        }
    }

    public abstract class DirectBatchStackOperation : BatchStackOperation
    {
        protected readonly IntValuedVariable RefId;

        public DirectBatchStackOperation(ChipWrapper chip, int lineNumber, string refId, string startAddress) : base(chip, lineNumber, startAddress)
        {
            RefId = new IntValuedVariable(chip.chip, lineNumber, refId, InstructionInclude.MaskIntValue, false);
        }

        protected void Load(out int refId, out int startAddress)
        {
            Load(out startAddress);
            refId = RefId.GetVariableValue(AliasTarget.Register);
        }
    }

    //start of our custom opcodes

    public class LoadSlotByDirectReference : ExtendedOpCode
    {
        private static readonly HelpString[] Args = { REGISTER, Constants.ID, SLOT_INDEX, LOGIC_SLOT_TYPE };
        public LoadSlotByDirectReference() : base("lsd") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length != 5) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
        }
        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new LSDInstance(chip, lineNumber, source[1], source[2], source[3], source[4]);
        }

        public override HelpString[] Params()
        {
            return Args;
        }

        public override string Description()
        {
            return "Functions identically to <color=yellow>ls</color>, with the exception of taking a device's <color=orange>ReferenceId</color> instead of a device pin.";
        }

        public class LSDInstance : Operation
        {
            protected readonly IndexVariable Store;
            protected readonly IntValuedVariable DeviceId;
            protected readonly IntValuedVariable SlotIndex;
            protected readonly EnumValuedVariable<LogicSlotType> LogicType;

            public LSDInstance(ChipWrapper chip, int lineNumber, string register, string referenceId, string slot, string logicType) : base(chip, lineNumber)
            {
                Store = new IndexVariable(chip.chip, lineNumber, register, InstructionInclude.MaskStoreIndex, false);
                DeviceId = new IntValuedVariable(chip.chip, lineNumber, referenceId, InstructionInclude.MaskIntValue, false);
                SlotIndex = new IntValuedVariable(chip.chip, lineNumber, slot, InstructionInclude.MaskIntValue , false);
                LogicType = new EnumValuedVariable<LogicSlotType>(chip.chip, lineNumber, logicType, InstructionInclude.MaskDoubleValue | InstructionInclude.LogicSlotType, false);
            }

            public override int Execute(int index)
            {
                int variableIndex = Store.GetVariableIndex(AliasTarget.Register);
                int slotIndex = SlotIndex.GetVariableValue(AliasTarget.Register);
                ILogicable logicableFromId = Chip.CircuitHousing.GetLogicableFromId(DeviceId.GetVariableValue(AliasTarget.Register));
                LogicSlotType logicType = LogicType.GetVariableValue(AliasTarget.Register);
                if(!logicableFromId.CanLogicRead(logicType, slotIndex)) {
                    throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectLogicSlotType, LineNumber);
                }
                Chip.Registers[variableIndex] = logicableFromId.GetLogicValue(logicType, slotIndex);
                return index + 1;
            }
        }
    }

    public class SetSlotByDirectReference : ExtendedOpCode
    {
        private static readonly HelpString[] Args = { Constants.ID, SLOT_INDEX, LOGIC_SLOT_TYPE, (REGISTER + NUMBER).Var("value") };
        public SetSlotByDirectReference() : base("ssd") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length != 5) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
        }
        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new SSDInstance(chip, lineNumber, source[1], source[2], source[3], source[4]);
        }

        public override HelpString[] Params()
        {
            return Args;
        }

        public override string Description()
        {
            return "Functions identically to <color=yellow>ss</color>, with the exception of taking a device's <color=orange>ReferenceId</color> instead of a device pin.";
        }

        public class SSDInstance : Operation
        {
            protected readonly IntValuedVariable DeviceId;
            protected readonly IntValuedVariable SlotIndex;
            protected readonly EnumValuedVariable<LogicSlotType> LogicType;
            protected readonly DoubleValueVariable Arg1;

            public SSDInstance(ChipWrapper chip, int lineNumber, string referenceId, string slot, string logicType, string registerOrValue) : base(chip, lineNumber)
            {
                DeviceId = new IntValuedVariable(chip.chip, lineNumber, referenceId, InstructionInclude.MaskIntValue, false);
                SlotIndex = new IntValuedVariable(chip.chip, lineNumber, slot, InstructionInclude.MaskDoubleValue, false);
                LogicType = new EnumValuedVariable<LogicSlotType>(chip.chip, lineNumber, logicType, InstructionInclude.MaskDoubleValue | InstructionInclude.LogicSlotType, false);
                Arg1 = new DoubleValueVariable(chip.chip, lineNumber, registerOrValue, InstructionInclude.MaskDoubleValue, false);
            }

            public override int Execute(int index)
            {
                int slotIndex = SlotIndex.GetVariableValue(AliasTarget.Register);
                double arg1 = Arg1.GetVariableValue(AliasTarget.Register);
                ILogicable logicableFromId = Chip.CircuitHousing.GetLogicableFromId(DeviceId.GetVariableValue(AliasTarget.Register));
                LogicSlotType logicType = LogicType.GetVariableValue(AliasTarget.Register);
                if (!(logicableFromId is ISlotWriteable slotWriteable))
                {
                    throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceNotSlotWriteable, LineNumber);
                }
                if(!slotWriteable.CanLogicWrite(logicType, slotIndex))
                {
                    throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectLogicSlotType, LineNumber);
                }
                slotWriteable.SetLogicValue(logicType, slotIndex, arg1);
                return index + 1;
            }
        }
    }

    //putb d? address prefabHash(r?|int)
    public class PutDevicesInBatch : ExtendedOpCode
    {
        public static readonly HelpString[] Args = { DEVICE_INDEX, Constants.ADDRESS, Constants.DEVICE_HASH };

        public PutDevicesInBatch() : base("putb") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length != 4)
            {
                throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
            }
        }

        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new Instance(chip, lineNumber, source[1], source[2], source[3]);
        }

        public override HelpString[] Params()
        {
            return Args;
        }

        public override string Description()
        {
            return "Gets the <color=orange>ReferenceId</color> of all devices with a matching <color=orange>PrefabHash</color>, then <color=yellow>put</color>s the " +
                "count of these addresses into the target device's stack at startAddress followed by each refId on subsequent stack addresses.";
        }

        public class Instance : DeviceBatchStackOperation
        {
            
            protected readonly IntValuedVariable PrefabHash;
            public Instance(ChipWrapper chip, int lineNumber, string deviceIndex, string startAddress, string prefabHash) : base(chip, lineNumber, deviceIndex, startAddress)
            {
                PrefabHash = new IntValuedVariable(chip.chip, lineNumber, prefabHash, InstructionInclude.MaskIntValue, false);
            }

            protected void Load(out int deviceIndex, out int networkIndex, out int startAddress, out int prefabHash)
            {
                Load(out deviceIndex, out networkIndex, out startAddress);
                prefabHash = PrefabHash.GetVariableValue(AliasTarget.Register);
            }

            protected override int TryExecute(int index)
            {
                Load(out var deviceIndex, out var networkIndex, out var address, out var prefabHash);
                var writeable = LoadWriteable(deviceIndex, networkIndex);
                
                List<ILogicable> batch = Chip.CircuitHousing.GetBatchOutput();
                if (batch == null) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceListNull, LineNumber);
                var filtered = batch.Where(device => 
                    {
                        var result = device.GetPrefabHash() == prefabHash;
                        return result;
                    }).Select(device => device.ReferenceId);
                
                Chip.CircuitHousing.HasPut();
                Utils.LoadToStack(writeable, filtered, address);
                
                return index + 1;
            }
        }
    }

    //putbn d? address prefabHash(r?|int) nameHash(r?|int)
    public class PutDevicesInBatchWithName : ExtendedOpCode
    {
        public static readonly HelpString[] Args = { DEVICE_INDEX, Constants.ADDRESS, Constants.DEVICE_HASH, Constants.NAME_HASH };
        public PutDevicesInBatchWithName() : base("putbn") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length != 5)
            {
                throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
            }
        }

        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new Instance(chip, lineNumber, source[1], source[2], source[3], source[4]);
        }

        public override HelpString[] Params()
        {
            return Args;
        }

        public override string Description()
        {
            return "Gets the <color=orange>ReferenceId</color> of all devices with a matching <color=orange>PrefabHash</color> and <color=orange>NameHash</color>, then <color=yellow>put</color>s the " +
                "count of these addresses into the target device's stack at startAddress followed by each refId on subsequent stack addresses.";
        }

        public class Instance : PutDevicesInBatch.Instance
        {
            protected readonly IntValuedVariable NameHash;
            
            public Instance(ChipWrapper chip, int lineNumber, string device, string address, string prefabHash, string nameHash) : base(chip, lineNumber, device, address, prefabHash)
            {
                NameHash = new IntValuedVariable(chip.chip, lineNumber, nameHash, InstructionInclude.MaskDoubleValue, false);
            }

            protected void Load(out int deviceIndex, out int networkIndex, out int address, out int prefabHash, out int nameHash)
            {
                Load(out deviceIndex, out networkIndex, out address, out prefabHash);
                nameHash = NameHash.GetVariableValue(AliasTarget.Register);
            }

            protected override int TryExecute(int index)
            {
                Load(out var deviceIndex, out var networkIndex, out var address, out var prefabHash, out var nameHash);
                var writeable = LoadWriteable(deviceIndex, networkIndex);

                List<ILogicable> batch = Chip.CircuitHousing.GetBatchOutput();
                if (batch == null) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceListNull, LineNumber);
                var filtered = batch.Where(device => device.GetPrefabHash() == prefabHash && device.GetNameHash() == nameHash).Select(device => device.ReferenceId);

                Chip.CircuitHousing.HasPut();
                Utils.LoadToStack(writeable, filtered, address);
                
                return index + 1;
            }
        }
    }

    //putdb id(r?|int) address prefabHash(r?|int)
    public class PutDirectlyDevicesInBatch : ExtendedOpCode
    {
        public static readonly HelpString[] Args = { Constants.ID, Constants.ADDRESS, Constants.DEVICE_HASH };

        public PutDirectlyDevicesInBatch() : base("putdb") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length != 4)
            {
                throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
            }
        }

        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new Instance(chip, lineNumber, source[1], source[2], source[3]);
        }

        public override HelpString[] Params()
        {
            return Args;
        }

        public override string Description()
        {
            return "Functions identically to <color=yellow>putb</color>, except the target device whose stack is modified is referenced by <color=orange>ReferenceId</color> instead of device pin.";
        }

        public class Instance : DirectBatchStackOperation
        {
            protected readonly IntValuedVariable PrefabHash;

            public Instance(ChipWrapper chip, int lineNumber, string refId, string startAddress, string prefabHash) : base(chip, lineNumber, refId, startAddress)
            {
                PrefabHash = new IntValuedVariable(chip.chip, lineNumber, prefabHash, InstructionInclude.MaskIntValue, false);
            }

            protected void Load(out int refId, out int startAddress, out int prefabHash)
            {
                Load(out refId, out startAddress);
                prefabHash = PrefabHash.GetVariableValue(AliasTarget.Register);
            }

            protected override int TryExecute(int index)
            {
                Load(out var refId, out var address, out var prefabHash);
                var writeable = LoadWriteable(refId);

                List<ILogicable> batch = Chip.CircuitHousing.GetBatchOutput();
                if (batch == null) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceListNull, LineNumber);
                var filtered = batch.Where(device => device.GetPrefabHash() == prefabHash).Select(device => device.ReferenceId);
                
                Chip.CircuitHousing.HasPut();
                Utils.LoadToStack(writeable, filtered, address);

                return index + 1;
            }
        }
    }

    //putdbn id(r?|int) address prefabHash(r?|int) nameHash(r?|int)
    public class PutDirectlyDevicesInBatchWithName : ExtendedOpCode
    {
        public static readonly HelpString[] Args = { Constants.ID, Constants.ADDRESS, Constants.DEVICE_HASH , Constants.NAME_HASH };

        public PutDirectlyDevicesInBatchWithName() : base("putdbn") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length != 5)
            {
                throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
            }
        }

        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new Instance(chip, lineNumber, source[1], source[2], source[3], source[4]);
        }

        public override HelpString[] Params()
        {
            return Args;
        }

        public override string Description()
        {
            return "Functions identically to <color=yellow>putbn</color>, except the target device whose stack is modified is referenced by <color=orange>ReferenceId</color> instead of device pin.";
        }

        public class Instance : PutDirectlyDevicesInBatch.Instance
        {
            protected readonly IntValuedVariable NameHash;
            public Instance(ChipWrapper chip, int lineNumber, string id, string address, string prefabHash, string nameHash) : base(chip, lineNumber, id, address, prefabHash)
            {
                NameHash = new IntValuedVariable(chip.chip, lineNumber, nameHash, InstructionInclude.MaskDoubleValue, false);
            }

            public void Load(out int refId, out int address, out int prefabHash, out int nameHash)
            {
                Load(out refId, out address, out prefabHash);
                nameHash = NameHash.GetVariableValue(AliasTarget.Register);
            }

            protected override int TryExecute(int index)
            {
                Load(out var refId, out var address, out var prefabHash, out var nameHash);
                var writeable = LoadWriteable(refId);

                List<ILogicable> batch = Chip.CircuitHousing.GetBatchOutput();
                if (batch == null) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceListNull, LineNumber);
                var filtered = batch.Where(device => device.GetPrefabHash() == prefabHash && device.GetNameHash() == nameHash).Select(device => device.ReferenceId);
                
                Chip.CircuitHousing.HasPut();
                Utils.LoadToStack(writeable, filtered, address);
                
                return index + 1;
            }
        }
    }

    //putgbn d? address <list of prefabHashes(r?|int)>
    public class PutDevicesGroupedByName : ExtendedOpCode
    {
        public static readonly HelpString[] RequiredArgs = { DEVICE_INDEX, Constants.ADDRESS, Constants.DEVICE_HASH };
        public static readonly HelpString VarArg = Constants.DEVICE_HASH.Optional();

        public PutDevicesGroupedByName() : base("putgbn") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length < 4) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
        }

        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new Instance(chip, lineNumber, source[1], source[2], source.Skip(3));
        }

        public override HelpString[] Params()
        {
            return RequiredArgs;
        }

        public override HelpString? VarArgParam()
        {
            return VarArg;
        }

        public override string Description()
        {
            return "Loads the <color=orange>ReferenceId</color>s of groups of devices that share the same name, and formats them into an array on the target device's stack." +
                "\n\nThis array is formatted with the count of groups at startAddress, followed by the reference of each device, ordered by the order of the prefab hashes passed to this command." +
                "\n\nGroups are only inlcuded in this array when exactly 1 device matches each prefabHash passed to this command. Devices sharing the same name, but whose hash is not included in the list are ignored." +
                "\n\nExample (with made up hashes): putgbn db 5 12 16" +
                "\n    Pressure Regulator prefabHash=16 refId=10 name=\"Fill Station 1\"" +
                "\n    Gas Tank Storage prefabHash=12 refId=11 name=\"Fill Station 1\"" +
                "\n    Pipe Heater prefabHash=14 refId=12 name=\"Fill Station 1\"" +
                "\n\n    Pressure Regulator prefabHash=16 refId=13 name=\"Fill Station 2\"" +
                "\n    Pressure Regulator prefabHash=16 refId=14 name=\"Fill Station 2\"" +
                "\n    Gas Tank Storage prefabHash=12 refId=15 name=\"Fill Station 2\"" +
                "\n\n    Gas Tank Storage prefabHash=12 refId=16 name=\"Fill Station 3\"" +
                "\n\nyields the following array:" +
                "\n5: 1\n6: 11\n7: 10";
        }

        public class Instance : DeviceBatchStackOperation
        {
            
            protected readonly List<IntValuedVariable> DeviceHashes = new List<IntValuedVariable>();

            public Instance(ChipWrapper chip, int lineNumber, string deviceIndex, string startAddress, IEnumerable<string> deviceHashes) : base(chip, lineNumber, deviceIndex, startAddress)
            {
                var uniques = new HashSet<string>();
                foreach (var deviceHash in deviceHashes)
                {
                    if (uniques.Contains(deviceHash)) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectVariable, lineNumber);
                    uniques.Add(deviceHash);
                    DeviceHashes.Add(new IntValuedVariable(chip.chip, lineNumber, deviceHash, InstructionInclude.MaskIntValue, false));
                }
            }

            protected void Load(out int deviceIndex, out int networkAddress, out int startAddress, out int[] prefabHashes)
            {
                Load(out deviceIndex, out networkAddress, out startAddress);
                prefabHashes = DeviceHashes.Select(deviceHash => deviceHash.GetVariableValue(AliasTarget.Register)).ToArray();
            }

            protected override int TryExecute(int index)
            {
                Load(out var deviceIndex, out var networkAddress, out var startAddress, out var prefabHashes);
                var writeable = LoadWriteable(deviceIndex, networkAddress);

                List<ILogicable> batch = Chip.CircuitHousing.GetBatchOutput() ?? throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceListNull, LineNumber);
                
                Chip.CircuitHousing.HasPut();
                Utils.LoadToStack(writeable, batch, prefabHashes, startAddress);

                return index + 1;
            }
        }
    }

    //putdgbn id(r?|int) address <list of prefabHashes(r?|int)>
    public class PutDirectlyDevicesGroupedByName : ExtendedOpCode
    {
        public static readonly HelpString[] RequiredArgs = { Constants.ID, Constants.ADDRESS, Constants.DEVICE_HASH };
        public static readonly HelpString VarArg = Constants.DEVICE_HASH.Optional();

        public PutDirectlyDevicesGroupedByName() : base("putdgbn") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length < 4) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
        }

        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new Instance(chip, lineNumber, source[1], source[2], source.Skip(3).ToArray());
        }

        public override HelpString[] Params()
        {
            return RequiredArgs;
        }

        public override HelpString? VarArgParam()
        {
            return VarArg;
        }

        public override string Description()
        {
            return "Functions identically to <color=yellow>putgbn</color>, except the target device whose stack is modified is referenced by <color=orange>ReferenceId</color> instead of device pin.";
        }

        public class Instance : DirectBatchStackOperation
        {
            protected readonly List<IntValuedVariable> DeviceHashes = new List<IntValuedVariable>();

            public Instance(ChipWrapper chip, int lineNumber, string refId, string startAddress, IEnumerable<string> deviceHashes) : base(chip, lineNumber, refId, startAddress)
            {
                var uniques = new HashSet<string>();
                foreach (var deviceHash in deviceHashes)
                {
                    if (uniques.Contains(deviceHash)) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectVariable, lineNumber);
                    uniques.Add(deviceHash);
                    DeviceHashes.Add(new IntValuedVariable(chip.chip, lineNumber, deviceHash, InstructionInclude.MaskIntValue, false));
                }
            }

            protected void Load(out int refId, out int startAddress, out int[] prefabHashes)
            {
                Load(out refId, out startAddress);
                prefabHashes = DeviceHashes.Select(deviceHash => deviceHash.GetVariableValue(AliasTarget.Register)).ToArray();
            }

            protected override int TryExecute(int index)
            {
                Load(out var refId, out var startAddress, out var prefabHashes);
                var writeable = LoadWriteable(refId);

                List<ILogicable> batch = Chip.CircuitHousing.GetBatchOutput() ?? throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceListNull, LineNumber);

                Chip.CircuitHousing.HasPut();
                Utils.LoadToStack(writeable, batch, prefabHashes, startAddress);

                return index + 1;
            }
        }
    }

    //clrr d? start(r?|int) count(r?|int)
    public class ClearRange : ExtendedOpCode
    {
        public static readonly HelpString[] Args = { DEVICE_INDEX, Constants.ADDRESS, Constants.COUNT };

        public ClearRange() : base("clrr") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length != 4) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
        }

        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new Instance(chip, lineNumber, source[1], source[2], source[3]);
        }

        public override HelpString[] Params()
        {
            return Args;
        }

        public override string Description()
        {
            return "Set all indexes on the device stack between startIndex (inclusive) and startIndex + count (inclusive) to 0.";
        }

        public class Instance : ClearRangeOperation
        {
            protected readonly DeviceIndexVariable DeviceIndex;
            public Instance(ChipWrapper chip, int lineNumber, string deviceIndex, string startAddress, string count) : base(chip, lineNumber, startAddress, count)
            {
                DeviceIndex = new DeviceIndexVariable(chip.chip, lineNumber, deviceIndex, InstructionInclude.MaskDeviceIndex, false);
            }

            protected override IMemoryWritable LoadWriteable()
            {
                var deviceIndex = DeviceIndex.GetVariableIndex(AliasTarget.Device);
                var networkIndex = DeviceIndex.GetNetworkIndex();

                var target = Chip.CircuitHousing.GetLogicableFromIndex(deviceIndex, networkIndex);
                if (target == null) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceNotFound, LineNumber);
                if (!(target is IMemoryWritable writeable)) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.MemoryNotWriteable, LineNumber);
                return writeable;
            }
        }
    }

    //clrdr
    public class ClearDirectlyRange : ExtendedOpCode
    {
        public static readonly HelpString[] Args = { Constants.ID, Constants.ADDRESS, Constants.COUNT };

        public ClearDirectlyRange() : base("clrdr") { }

        public override void Accept(int lineNumber, string[] source)
        {
            if (source.Length != 4) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
        }

        public override Operation Create(ChipWrapper chip, int lineNumber, string[] source)
        {
            return new Instance(chip, lineNumber, source[1], source[2], source[3]);
        }

        public override HelpString[] Params()
        {
            return Args;
        }

        public override string Description()
        {
            return "Functions identically to <color=yellow>clrr</color>, except the target device whose stack is modified is referenced by <color=orange>ReferenceId</color> instead of device pin.";
        }

        public class Instance : ClearRangeOperation
        {
            protected readonly IntValuedVariable RefId;
            public Instance(ChipWrapper chip, int lineNumber, string refId, string startAddress, string count) : base(chip, lineNumber, startAddress, count)
            {
                RefId = new IntValuedVariable(chip.chip, lineNumber, refId, InstructionInclude.MaskIntValue, false);
            }

            protected override IMemoryWritable LoadWriteable()
            {
                var refId = RefId.GetVariableValue(AliasTarget.Register);

                var target = Chip.CircuitHousing.GetLogicableFromId(refId);
                if (target == null) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.DeviceNotFound, LineNumber);
                if (!(target is IMemoryWritable writeable)) throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.MemoryNotWriteable, LineNumber);
                return writeable;
            }
        }
    }
}
