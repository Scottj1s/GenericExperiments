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
        using Proj = WinRT;

        struct NonBlittable
        {
            public Blittable b;
            public IntPtr s;

            public static Proj.NonBlittable FromAbi(NonBlittable arg)
            {
                return new Proj.NonBlittable() 
                { 
                    b = arg.b, 
                    s = MarshalString.FromAbi(arg.s)
                };
            }

            public static NonBlittable ToAbi(Proj.NonBlittable arg)
            {
                return new NonBlittable()
                {
                    b = arg.b,
                    s = MarshalString.ToAbi(arg.s)
                };
            }
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

    struct MarshalArray<T>
    {
        public static T FromAbi(IntPtr value)
        {
            // todo: convert abi to array 
            var elem_type = typeof(T).GetElementType();
            Array a = Array.CreateInstance(elem_type, (int)value);
            return (T)(object)a;
            
            //if (elem_type == typeof(int))
            //{
            //    return (T)(object)(new[] { 42, 1729 });
            //}
            //else if (elem_type == typeof(string))
            //{
            //    return (T)(object)(new[] { "foo", "bar" });
            //}
            //else if (elem_type == typeof(Blittable))
            //{
            //    Blittable blittable_value = new Blittable { i = 42, d = 1729 };
            //    Blittable[] blittables = new[] { blittable_value, blittable_value };
            //    return (T)(object)blittables;
            //}
            //else if (elem_type == typeof(NonBlittable))
            //{
            //    NonBlittable nonblittable_value = new NonBlittable { b = { i = 42, d = 1729 }, s = "foo" };
            //    NonBlittable[] nonblittables = new[] { nonblittable_value, nonblittable_value };
            //    return (T)(object)nonblittables;
            //}
            //throw new InvalidOperationException();
        }
        public static IntPtr ToAbi(T value)
        {
            // todo: convert array to abi
            Array a = (Array)(object)value;
            return new IntPtr(a.Length);
        }
    }

    public class Marshaler<T>
    {
        static Marshaler()
        {
            Type type = typeof(T);

            if (type.IsValueType)
            {
                // If type not blittable, bind to ABI counterpart's FromAbi, ToAbi
                AbiType = Type.GetType(type.Namespace + ".ABI." + type.Name);
                if (AbiType != null)
                {
                    FromAbi = BindFromAbi(AbiType);
                    ToAbi = BindToAbi(AbiType);
                }
                else 
                { 
                    // assert type.IsBlittable()
                    AbiType = type;
                    FromAbi = (object value) => (T)value;
                    ToAbi = (T value) => value;
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

        private static Func<object, T> BindFromAbi(Type AbiType)
        {
            var parms = new[] { Expression.Parameter(typeof(object), "arg") };
            return Expression.Lambda<Func<object, T>>(
                Expression.Call(AbiType.GetMethod("FromAbi"), 
                    new[] { Expression.Convert(parms[0], AbiType) }),
                parms).Compile();
        }

        private static Func<T, object> BindToAbi(Type AbiType)
        {
            var parms = new[] { Expression.Parameter(typeof(T), "arg") };
            return Expression.Lambda<Func<T, object>>(
                Expression.Convert(Expression.Call(AbiType.GetMethod("ToAbi"), parms), 
                typeof(object)), parms).Compile();
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
        static private readonly Type out_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.AbiType.MakeByRefType() });
        static private readonly Type ref_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.AbiType.MakeByRefType() });

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

        static void Report(string test, bool success) => System.Console.WriteLine("{0} {1}working", test, success ? "" : "not ");

        static unsafe void Main(string[] args)
        {
            int int_value = 42;
            int[] ints = new[] { 42, 1729 };
            string string_value = "foo";
            string[] strings = new[] { "foo", "bar" };
            Blittable blittable_value = new Blittable { i = 42, d = 1729 };
            Blittable[] blittables = new[] { blittable_value, blittable_value };
            NonBlittable nonblittable_value = new NonBlittable { b = { i = 42, d = 1729 }, s = "foo" };
            NonBlittable[] nonblittables = new[] { nonblittable_value, nonblittable_value };

            // Int methods
            int out_int = 42;
            int ref_int = 1729;
            var gi = new Generic<int>(IntPtr.Zero,
                GetFP<GetInt>((IntPtr @this) => int_value),
                GetFP<PutInt>((IntPtr @this, int arg) => int_value = arg),
                GetFP<OutInt>((IntPtr @this, out int arg) => arg = out_int),
                GetFP<RefInt>((IntPtr @this, ref int arg) => arg = ref_int)
            );
            int i = gi.Get();
            gi.Put(i);
            i = 0;
            gi.Out(out i);
            Report("out int", i == out_int);
            gi.Ref(ref i);
            Report("ref int", i == ref_int);

            // String (IntPtr) methods 
            var out_string = "foo";
            var ref_string = "bar";
            var gs = new Generic<string>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalString.ToAbi(string_value)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => string_value = MarshalString.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalString.ToAbi(out_string)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalString.ToAbi(ref_string))
            );
            string s = gs.Get();
            gs.Put(s);
            s = "";
            gs.Out(out s);
            Report("out string", s == out_string);
            gs.Ref(ref s);
            Report("ref string", s == ref_string);

            // Blittable struct methods 
            Blittable out_blittable = new Blittable { i = 42, d = 1729 };
            Blittable ref_blittable = new Blittable { i = 1729, d = 42 };
            var gb = new Generic<Blittable>(IntPtr.Zero,
                GetFP<GetBlittable>((IntPtr @this) => blittable_value),
                GetFP<PutBlittable>((IntPtr @this, Blittable arg) => blittable_value = arg),
                GetFP<OutBlittable>((IntPtr @this, out Blittable arg) => arg = out_blittable),
                GetFP<RefBlittable>((IntPtr @this, ref Blittable arg) => arg = ref_blittable)
            );
            Blittable b = gb.Get();
            gb.Put(b);
            b = new Blittable();
            gb.Out(out b);
            Report("out blittable", b.Equals(out_blittable));
            gb.Ref(ref b);
            Report("ref blittable", b.Equals(ref_blittable));

            // NonBlittable struct methods 
            NonBlittable out_nonblittable = new NonBlittable { b = { i = 42, d = 1729 }, s = "foo" };
            NonBlittable ref_nonblittable = new NonBlittable { b = { i = 1729, d = 42 }, s = "bar" };
            var gn = new Generic<NonBlittable>(IntPtr.Zero,
                GetFP<GetNonBlittable>((IntPtr @this) => ABI.NonBlittable.ToAbi(nonblittable_value)),
                GetFP<PutNonBlittable>((IntPtr @this, ABI.NonBlittable arg) => nonblittable_value = ABI.NonBlittable.FromAbi(arg)),
                GetFP<OutNonBlittable>((IntPtr @this, out ABI.NonBlittable arg) => arg = ABI.NonBlittable.ToAbi(out_nonblittable)),
                GetFP<RefNonBlittable>((IntPtr @this, ref ABI.NonBlittable arg) => arg = ABI.NonBlittable.ToAbi(ref_nonblittable))
            );
            NonBlittable n = gn.Get();
            gn.Put(n);
            n = new NonBlittable();
            gn.Out(out n);
            Report("out nonblittable", b.Equals(out_nonblittable));
            gn.Ref(ref n);
            Report("ref nonblittable", b.Equals(ref_nonblittable));

            // Int array methods
            var out_ints = new int[1];
            var ref_ints = new int[2];
            var gia = new Generic<int[]>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalArray<int[]>.ToAbi(ints)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => ints = MarshalArray<int[]>.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<int[]>.ToAbi(out_ints)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<int[]>.ToAbi(ref_ints))
            );
            int[] ia = gia.Get();
            gia.Put(ia);
            ia = null;
            gia.Out(out ia);
            Report("out int array", ia.Length == out_ints.Length);
            gia.Ref(ref ia);
            Report("ref int array", ia.Length == ref_ints.Length);

            // String array (IntPtr) methods
            var out_strings = new string[3];
            var ref_strings = new string[4];
            var gsa = new Generic<string[]>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalArray<string[]>.ToAbi(strings)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => strings = MarshalArray<string[]>.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<string[]>.ToAbi(out_strings)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<string[]>.ToAbi(ref_strings))
            );
            string[] sa = gsa.Get();
            gsa.Put(sa);
            gsa.Out (out sa);
            Report("out string array", sa.Length == out_strings.Length);
            gsa.Ref(ref sa);
            Report("ref string array", sa.Length == ref_strings.Length);

            // Blittable array (IntPtr) methods
            var out_blittables = new Blittable[5];
            var ref_blittables = new Blittable[6];
            var gba = new Generic<Blittable[]>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalArray<Blittable[]>.ToAbi(blittables)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => blittables = MarshalArray<Blittable[]>.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<Blittable[]>.ToAbi(out_blittables)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<Blittable[]>.ToAbi(ref_blittables))
            );
            Blittable[] ba = gba.Get();
            gba.Put(ba);
            gba.Out(out ba);
            Report("out blittable array", ba.Length == out_blittables.Length);
            gba.Ref(ref ba);
            Report("ref blittable array", ba.Length == ref_blittables.Length);

            // NonBlittable array (IntPtr) methods
            var out_nonblittables = new NonBlittable[5];
            var ref_nonblittables = new NonBlittable[6];
            var gna = new Generic<NonBlittable[]>(IntPtr.Zero,
                GetFP<GetPtr>((IntPtr @this) => MarshalArray<NonBlittable[]>.ToAbi(nonblittables)),
                GetFP<PutPtr>((IntPtr @this, IntPtr arg) => nonblittables = MarshalArray<NonBlittable[]>.FromAbi(arg)),
                GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<NonBlittable[]>.ToAbi(out_nonblittables)),
                GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<NonBlittable[]>.ToAbi(ref_nonblittables))
            );
            NonBlittable[] na = gna.Get();
            gna.Put(na);
            gna.Out(out na);
            Report("out nonblittable array", na.Length == out_nonblittables.Length);
            gna.Ref(ref na);
            Report("ref nonblittable array", na.Length == ref_nonblittables.Length);
        }
    }
}
