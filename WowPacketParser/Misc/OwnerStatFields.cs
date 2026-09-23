using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using WowPacketParser.Proto;
using WowPacketParser.Store.Objects.UpdateFields;

namespace WowPacketParser.Misc
{
    /// <summary>
    /// Copies a unit's stat sheet - damage range, attack power, stats, resistance buffs - into the
    /// proto. The server sends these only to the unit's owner, charmer or rider, so IUnitData
    /// leaves them out, and the generated UnitData classes that carry them are regenerated rather
    /// than edited. They are read by property name instead, compiled once per concrete type; a
    /// type without them, the legacy field-array one included, copies nothing.
    /// </summary>
    public static class OwnerStatFields
    {
        private static readonly (string Name, Action<UpdateValuesUnitDataFields, float?> Set)[] Floats =
        {
            ("MinDamage", (u, v) => u.MinDamage = v),
            ("MaxDamage", (u, v) => u.MaxDamage = v),
            ("MinOffHandDamage", (u, v) => u.MinOffHandDamage = v),
            ("MaxOffHandDamage", (u, v) => u.MaxOffHandDamage = v),
            ("MinRangedDamage", (u, v) => u.MinRangedDamage = v),
            ("MaxRangedDamage", (u, v) => u.MaxRangedDamage = v),
            ("AttackPowerMultiplier", (u, v) => u.AttackPowerMultiplier = v),
        };

        private static readonly (string Name, Action<UpdateValuesUnitDataFields, int?> Set)[] Ints =
        {
            ("AttackPower", (u, v) => u.AttackPower = v),
            ("AttackPowerModPos", (u, v) => u.AttackPowerModPos = v),
            ("AttackPowerModNeg", (u, v) => u.AttackPowerModNeg = v),
            ("RangedAttackPower", (u, v) => u.RangedAttackPower = v),
            ("RangedAttackPowerModPos", (u, v) => u.RangedAttackPowerModPos = v),
            ("RangedAttackPowerModNeg", (u, v) => u.RangedAttackPowerModNeg = v),
        };

        private static readonly (string Name, Func<UpdateValuesUnitDataFields, Google.Protobuf.Collections.RepeatedField<Int32ValueWrapper>> Field)[] Arrays =
        {
            ("Stats", u => u.Stats),
            ("StatPosBuff", u => u.StatPosBuff),
            ("StatNegBuff", u => u.StatNegBuff),
            ("ResistanceBuffModsPositive", u => u.ResistanceBuffModsPositive),
            ("ResistanceBuffModsNegative", u => u.ResistanceBuffModsNegative),
        };

        private sealed class Readers
        {
            public Func<object, float?>[] Floats;
            public Func<object, int?>[] Ints;
            public Func<object, int?[]>[] Arrays;
            public bool Any;
        }

        private static readonly ConcurrentDictionary<Type, Readers> Cache = new();

        private static Func<object, T> Getter<T>(Type type, string name)
        {
            var property = type.GetProperty(name);
            if (property == null || property.PropertyType != typeof(T) || !property.CanRead)
                return null;

            var instance = Expression.Parameter(typeof(object));
            return Expression.Lambda<Func<object, T>>(
                Expression.Property(Expression.Convert(instance, type), property), instance).Compile();
        }

        private static Readers Build(Type type)
        {
            var readers = new Readers
            {
                Floats = Floats.Select(f => Getter<float?>(type, f.Name)).ToArray(),
                Ints = Ints.Select(i => Getter<int?>(type, i.Name)).ToArray(),
                Arrays = Arrays.Select(a => Getter<int?[]>(type, a.Name)).ToArray()
            };
            readers.Any = readers.Floats.Any(f => f != null) || readers.Ints.Any(i => i != null) ||
                          readers.Arrays.Any(a => a != null);
            return readers;
        }

        public static void Fill(UpdateValuesUnitDataFields unit, IUnitData data)
        {
            var readers = Cache.GetOrAdd(data.GetType(), Build);
            if (!readers.Any)
                return;

            for (var i = 0; i < Floats.Length; i++)
            {
                var value = readers.Floats[i]?.Invoke(data);
                if (value != null)
                    Floats[i].Set(unit, value);
            }

            for (var i = 0; i < Ints.Length; i++)
            {
                var value = readers.Ints[i]?.Invoke(data);
                if (value != null)
                    Ints[i].Set(unit, value);
            }

            for (var i = 0; i < Arrays.Length; i++)
            {
                // Every unit's create and update carries these arrays, all null unless the
                // receiver owns the unit; copying the nulls would only bloat the proto.
                var source = readers.Arrays[i]?.Invoke(data);
                if (source == null || source.All(v => v == null))
                    continue;

                var field = Arrays[i].Field(unit);
                for (var j = 0; j < source.Length; j++)
                {
                    if (j < field.Count)
                    {
                        if (source[j] != null)
                            field[j] = new Int32ValueWrapper { Value = source[j] };
                    }
                    else
                        field.Add(new Int32ValueWrapper { Value = source[j] });
                }
            }
        }
    }
}
