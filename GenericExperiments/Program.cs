using System;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;

namespace WinRT
{
    struct Blittable
    {
        public int i;
        public double d;
    }

    struct NonBlittable
    {
        public Blittable b;
        public string s;
    }

    namespace ABI
    { 
        struct NonBlittable
        {
            public Blittable b;
            public IntPtr s;
        }
    }

    public static class TypeExtensions
    {
        public static bool IsDelegate(this Type type)
        {
            return typeof(MulticastDelegate).IsAssignableFrom(type.BaseType);
        }

        // this should be implicit (cswinrt can figure it out)
        public static bool IsBlittable(this Type type)
        {
            if (type.IsPrimitive)
            {
                return true;
            }
            if (type.IsGenericType || !type.IsValueType)
            {
                return false;
            }
            return type.GetFields().All((fi) => fi.FieldType.IsBlittable());
        }
    }

    struct MarshalString
    {
        private static IntPtr Foo = IntPtr.Zero;
        private static IntPtr Bar = new IntPtr(1);

        public static string FromAbi(IntPtr value) => (IntPtr)value == Foo ? "foo" : "bar";
        public static IntPtr ToAbi(string value) => value == "foo" ? Foo : Bar;
    }

    struct MarshalNonBlittable
    {
        public static NonBlittable FromAbi(ABI.NonBlittable value) => new NonBlittable() { b = value.b, s = MarshalString.FromAbi(value.s) };
        public static ABI.NonBlittable ToAbi(NonBlittable value) => new ABI.NonBlittable() { b = value.b, s = MarshalString.ToAbi(value.s) };
    }

    struct MarshalArray<T>
    {
        public static T FromAbi(IntPtr value)
        {
            var elem_type = typeof(T).GetElementType();
            if (elem_type == typeof(int))
            {
                return (T)(object)(new[] { 42, 1729 });
            }
            else if (elem_type == typeof(string))
            {
                return (T)(object)(new[] { "foo", "bar" });
            }
            else if (elem_type == typeof(Blittable))
            {
                Blittable blittable_value = new Blittable { i = 42, d = 1729 };
                Blittable[] blittable_array = new[] { blittable_value, blittable_value };
                return (T)(object)blittable_array;
            }
            else if (elem_type == typeof(NonBlittable))
            {
                NonBlittable nonblittable_value = new NonBlittable { b = { i = 42, d = 1729 }, s = "foo" };
                NonBlittable[] nonblittable_array = new[] { nonblittable_value, nonblittable_value };
                return (T)(object)nonblittable_array;
            }
            throw new InvalidOperationException();
        }
        public static IntPtr ToAbi(T value) => IntPtr.Zero;
    }

    public class Marshaler<T>
    {
        static Marshaler()
        {
            Type type = typeof(T);

            if (type.IsValueType)
            {
                if (type.IsBlittable())
                {
                    AbiType = type;
                    FromAbi = (object value) => (T)value;
                    ToAbi = (T value) => value;
                }
                else
                {
                    AbiType = Type.GetType(type.Namespace + ".ABI." + type.Name);
                    // todo: reflection-based recursive struct conversion 
                    FromAbi = (object value) => (T)(object)MarshalNonBlittable.FromAbi((ABI.NonBlittable)value);
                    ToAbi = (T value) => (ABI.NonBlittable)MarshalNonBlittable.ToAbi((NonBlittable)(object)value);
                }
            }
            else if (type.IsArray)
            {
                var elem_type = type.GetElementType();
                if (elem_type.IsBlittable())
                {
                    // todo: allocate array directly from elements
                    AbiType = typeof(IntPtr);
                    FromAbi = (object value) => (T)(object)MarshalArray<T>.FromAbi((IntPtr)value);
                    ToAbi = (T value) => (object)MarshalArray<T>.ToAbi(value);
                }
                else 
                {
                    // allocate array and convert elements
                    AbiType = typeof(IntPtr);
                    FromAbi = (object value) => (T)(object)MarshalArray<T>.FromAbi((IntPtr)value);
                    ToAbi = (T value) => (object)MarshalArray<T>.ToAbi(value);
                }
            }
            else if (type == typeof(String))
            {
                AbiType = typeof(IntPtr);
                FromAbi = (object value) => (T)(object)MarshalString.FromAbi((IntPtr)value);
                ToAbi = (T value) => MarshalString.ToAbi((string)(object)value);
            }
            else // IInspectables (rcs, interfaces, delegates)
            {
                AbiType = typeof(IntPtr);
                FromAbi = (object value) => (T)value;
                ToAbi = (T value) => (object)value;
            }
        }

        public static readonly Type AbiType; 
        public static readonly Func<object, T> FromAbi;
        public static readonly Func<T, object> ToAbi;
        public static object OutAbi() => Activator.CreateInstance(AbiType);
    }

    class Generic<T>
    {
        static private readonly Marshaler<T> marshaler_T = new Marshaler<T>();

        static private readonly Type get_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.AbiType });
        static private readonly Type put_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.AbiType });
        static private readonly Type out_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.AbiType });
        static private readonly Type ref_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.AbiType });

        private readonly IntPtr @this;
        private readonly Delegate get_del;
        private readonly Delegate put_del;
        private readonly Delegate out_del;
        private readonly Delegate ref_del;

        public Generic(IntPtr @this,
            IntPtr get_fp, IntPtr put_fp, IntPtr out_fp, IntPtr ref_fp)
        {
            this.@this = @this;
            get_del = Marshal.GetDelegateForFunctionPointer(get_fp, get_type);
            put_del = Marshal.GetDelegateForFunctionPointer(put_fp, put_type);
            out_del = Marshal.GetDelegateForFunctionPointer(out_fp, out_type);
            ref_del = Marshal.GetDelegateForFunctionPointer(ref_fp, ref_type);
        }

        public T Get()
        {
            return Marshaler<T>.FromAbi(get_del.DynamicInvoke(new object[] { @this }));
        }

        public void Put(T value)
        {
            put_del.DynamicInvoke(new object[] { @this, Marshaler<T>.ToAbi(value) });
        }

        public void Out(out T value)
        {
            object[] parameters = new object[] { @this, Marshaler<T>.OutAbi() };
            out_del.DynamicInvoke(parameters);
            value = Marshaler<T>.FromAbi(parameters[1]);
        }

        public void Ref(ref T value)
        {
            object[] parameters = new object[] { @this, Marshaler<T>.ToAbi(value) };
            ref_del.DynamicInvoke(parameters);
            value = Marshaler<T>.FromAbi(parameters[1]);
        }
    }

    class Program
    {
        delegate int GetInt(IntPtr @this);
        delegate void PutInt(IntPtr @this, int i);
        delegate void OutInt(IntPtr @this, out int i);
        delegate void RefInt(IntPtr @this, ref int i);

        delegate IntPtr GetPtr(IntPtr @this);
        delegate void PutPtr(IntPtr @this, IntPtr p);
        delegate void OutPtr(IntPtr @this, out IntPtr p);
        delegate void RefPtr(IntPtr @this, ref IntPtr p);

        delegate Blittable GetBlittable(IntPtr @this);
        delegate void PutBlittable(IntPtr @this, Blittable b);
        delegate void OutBlittable(IntPtr @this, out Blittable b);
        delegate void RefBlittable(IntPtr @this, ref Blittable b);

        delegate ABI.NonBlittable GetNonBlittable(IntPtr @this);
        delegate void PutNonBlittable(IntPtr @this, ABI.NonBlittable b);
        delegate void OutNonBlittable(IntPtr @this, out ABI.NonBlittable b);
        delegate void RefNonBlittable(IntPtr @this, ref ABI.NonBlittable b);

        static public IntPtr GetFP<T>(T del) => Marshal.GetFunctionPointerForDelegate<T>(del);

        static unsafe void Main(string[] args)
        {
            int int_value = 42;
            int[] int_array = new[] { 42, 1729 };
            string string_value = "foo";
            string[] string_array = new[] { "foo", "bar" };
            Blittable blittable_value = new Blittable { i = 42, d = 1729 };
            Blittable[] blittable_array = new[] { blittable_value, blittable_value };
            NonBlittable nonblittable_value = new NonBlittable { b = { i = 42, d = 1729 }, s = "foo" };
            NonBlittable[] nonblittable_array = new[] { nonblittable_value, nonblittable_value };

            var gi = new Generic<int>(IntPtr.Zero,
                GetFP<GetInt>((IntPtr @this) => int_value),
                GetFP<PutInt>((IntPtr @this, int arg) => int_value = arg),
                GetFP<OutInt>((IntPtr @this, out int arg) => arg = int_value),
                GetFP<RefInt>((IntPtr @this, ref int arg) => arg = int_value)
            );
            int i = gi.Get();
            gi.Put(i);
            gi.Out(out i);
            gi.Ref(ref i);

            var gs = new Generic<string>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalString.ToAbi(string_value)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => string_value = MarshalString.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalString.ToAbi(string_value)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalString.ToAbi(string_value))
            );
            string s = gs.Get();
            gs.Put(s);
            gs.Out(out s);
            gs.Ref(ref s);

            var gb = new Generic<Blittable>(IntPtr.Zero,
                GetFP<GetBlittable>((IntPtr @this) => blittable_value),
                GetFP<PutBlittable>((IntPtr @this, Blittable arg) => blittable_value = arg),
                GetFP<OutBlittable>((IntPtr @this, out Blittable arg) => arg = blittable_value),
                GetFP<RefBlittable>((IntPtr @this, ref Blittable arg) => arg = blittable_value)
            );
            Blittable b = gb.Get();
            gb.Put(b);
            gb.Out(out b);
            gb.Ref(ref b);

            var gn = new Generic<NonBlittable>(IntPtr.Zero,
                GetFP<GetNonBlittable>((IntPtr @this) => MarshalNonBlittable.ToAbi(nonblittable_value)),
                GetFP<PutNonBlittable>((IntPtr @this, ABI.NonBlittable arg) => nonblittable_value = MarshalNonBlittable.FromAbi(arg)),
                GetFP<OutNonBlittable>((IntPtr @this, out ABI.NonBlittable arg) => arg = MarshalNonBlittable.ToAbi(nonblittable_value)),
                GetFP<RefNonBlittable>((IntPtr @this, ref ABI.NonBlittable arg) => arg = MarshalNonBlittable.ToAbi(nonblittable_value))
            );
            NonBlittable n = gn.Get();
            gn.Put(n);
            gn.Out(out n);
            gn.Ref(ref n);

            var gia = new Generic<int[]>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalArray<int[]>.ToAbi(int_array)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => int_array = MarshalArray<int[]>.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<int[]>.ToAbi(int_array)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<int[]>.ToAbi(int_array))
            );
            int[] ia = gia.Get();
            gia.Put(ia);
            gia.Out(out ia);
            gia.Ref(ref ia);

            var gsa = new Generic<string[]>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalArray<string[]>.ToAbi(string_array)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => string_array = MarshalArray<string[]>.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<string[]>.ToAbi(string_array)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<string[]>.ToAbi(string_array))
            );
            string[] sa = gsa.Get();
            gsa.Put(sa);
            gsa.Out (out sa);
            gsa.Ref(ref sa);

            var gba = new Generic<Blittable[]>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalArray<Blittable[]>.ToAbi(blittable_array)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => blittable_array = MarshalArray<Blittable[]>.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<Blittable[]>.ToAbi(blittable_array)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<Blittable[]>.ToAbi(blittable_array))
            );
            Blittable[] ba = gba.Get();
            gba.Put(ba);
            gba.Out(out ba);
            gba.Ref(ref ba);

            var gna = new Generic<NonBlittable[]>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalArray<NonBlittable[]>.ToAbi(nonblittable_array)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => nonblittable_array = MarshalArray<NonBlittable[]>.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<NonBlittable[]>.ToAbi(nonblittable_array)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<NonBlittable[]>.ToAbi(nonblittable_array))
            );
            NonBlittable[] na = gna.Get();
            gna.Put(na);
            gna.Out(out na);
            gna.Ref(ref na);
        }
    }
}
