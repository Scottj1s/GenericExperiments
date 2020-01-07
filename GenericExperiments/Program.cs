// for testing purposes, don't attempt to comemtaskfree a pinned managed object address 
#define TEST_PASS_THRU

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Collections.Generic;
using System.IO;

namespace WinRT
{
    public static class GCExtensions
    {
        public static void Dispose(this GCHandle handle)
        {
            if (handle.IsAllocated)
            { 
                handle.Free();
            }
        }
    }

    struct Blittable
    {
        public int i;
        public double d;
    }

    struct NonBlittable
    {
        public Blittable b;
        public string s1;
        public string s2;
    }

    namespace ABI
    {
        using Proj = WinRT;

        struct NonBlittable
        {
            public Blittable b;
            public IntPtr s1;
            public IntPtr s2;

            public struct Cache
            {
                public NonBlittable abi;
                public MarshalString.Cache s1;
                public MarshalString.Cache s2;
                public bool Dispose()
                {
                    MarshalString.DisposeCache(s1);
                    MarshalString.DisposeCache(s2);
                    return false;
                }
            }

            public static Cache CreateCache(Proj.NonBlittable arg)
            {
                Cache cache = new Cache();
                try 
                {
                    cache.s1 = MarshalString.CreateCache(arg.s1);
                    cache.s2 = MarshalString.CreateCache(arg.s2);
                    cache.abi = new NonBlittable()
                    {
                        b = arg.b,
                        s1 = MarshalString.GetAbi(cache.s1),
                        s2 = MarshalString.GetAbi(cache.s2)
                    };
                    return cache;
                }
                catch (Exception) when (cache.Dispose())
                {
                    // Will never execute 
                    return default;
                }
            }

            public static NonBlittable GetAbi(Cache cache) => cache.abi;

            public static Proj.NonBlittable FromAbi(NonBlittable arg)
            {
                return new Proj.NonBlittable()
                {
                    b = arg.b,
                    s1 = MarshalString.FromAbi(arg.s1),
                    s2 = MarshalString.FromAbi(arg.s2)
                };
            }

            public static void DisposeCache(Cache cache) => cache.Dispose();
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

    internal class Platform
    {
        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern unsafe int WindowsCreateStringReference(char* sourceString,
                                                  int length,
                                                  [Out] IntPtr* hstring_header,
                                                  [Out] IntPtr* hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern unsafe char* WindowsGetStringRawBuffer(IntPtr hstring, [Out] uint* length);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern int WindowsDeleteString(IntPtr hstring);
    }

    internal static class Box
    {
        private class BoxedValue<T>
        { 
            public BoxedValue(T value)
            {
                _value = value;
            }
            public T _value;
        }

        internal class RequireStruct<U> where U : struct { }
        internal class RequireClass<U> where U : class { }

        internal static object BoxValue<U>(U value, RequireStruct<U> _ = null) where U : struct
        {
            return new BoxedValue<U>(value);
        }

        internal static object BoxValue<U>(U value, RequireClass<U> _ = null) where U : class
        {
            return value;
        }

        internal static U UnboxValue<U>(object box, RequireStruct<U> _ = null) where U : struct
        {
            return ((BoxedValue<U>)box)._value;
        }

        internal static U UnboxValue<U>(object value, RequireClass<U> _ = null) where U : class
        {
            return (U)value;
        }
    }

    struct MarshalString
    {
        public class Cache
        {
            public bool Dispose()
            { 
                _gchandle.Dispose();
                return false;
            }
            
            public unsafe struct HStringHeader // sizeof(HSTRING_HEADER)
            {
                public fixed byte Reserved[24];
            };
            public HStringHeader _header;
            public GCHandle _gchandle;
            public IntPtr _handle;
        }

        public static unsafe Cache CreateCache(string value)
        {
            Cache cache = new Cache();
            try
            {
                cache._gchandle = GCHandle.Alloc(value, GCHandleType.Pinned);
                fixed (void* chars = value, header = &cache._header, handle = &cache._handle)
                {
                    Marshal.ThrowExceptionForHR(Platform.WindowsCreateStringReference(
                        (char*)chars, value.Length, (IntPtr*)header, (IntPtr*)handle));
                };
                return cache;
            }
            catch (Exception) when (cache.Dispose())
            {
                // Will never execute 
                return default;
            }
        }

        public static IntPtr GetAbi(Cache cache) => cache._handle;
        public static IntPtr GetAbi(object box) => Box.UnboxValue<Cache>(box)._handle;

        public static void DisposeCache(Cache cache) => cache.Dispose();
            // no need to delete hstring reference
        public static void DisposeCache(object box) 
        {
            if (box != null)
                DisposeCache(Box.UnboxValue<Cache>(box));
        }

        public static void DisposeAbi(IntPtr hstring)
        {
            if(hstring != IntPtr.Zero)
                Platform.WindowsDeleteString(hstring);
        }
        public static void DisposeAbi(object abi)
        {
            if (abi != null)
                DisposeAbi(((IntPtr)abi));
        }

        public static string FromAbi(IntPtr value)
        {
            unsafe
            {
                uint length;
                var buffer = Platform.WindowsGetStringRawBuffer(value, &length);
                return new string(buffer, 0, (int)length);
            }
        }
    }

    struct MarshalBlittableArray
    {
        public struct Cache
        {
            public Cache(Array array) => _gchandle = GCHandle.Alloc(array, GCHandleType.Pinned);
            public void Dispose() => _gchandle.Dispose();
            
            public GCHandle _gchandle;
        };

        public static Cache CreateCache(Array array) => new Cache(array);
        public static (IntPtr data, int length) GetAbi(object box)
        {
            var cache = (Cache)box;
            return (cache._gchandle.AddrOfPinnedObject(), ((Array)cache._gchandle.Target).Length);
        }
        public static unsafe T[] FromAbi<T>(object box)
        {
            var abi = ((IntPtr data, int length))box;
            Array array = Array.CreateInstance(typeof(T), abi.length);
            var array_handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            var array_data = array_handle.AddrOfPinnedObject();
            var byte_length = abi.length * Marshal.SizeOf<T>();
            Buffer.MemoryCopy(abi.data.ToPointer(), array_data.ToPointer(), byte_length, byte_length);
            return (T[])array;
        }
        public static void DisposeCache(object box) => ((Cache)box).Dispose();
        
        public static void DisposeAbi(object box)
        {
#if !TEST_PASS_THRU
            var abi = ((IntPtr data, int length))box;
            Marshal.FreeCoTaskMem(abi.data);
#endif
        }
    }

    struct MarshalNonBlittableArray<T>
    {
        public struct Cache
        {
            public bool Dispose()
            {
                _gchandle.Dispose();
                foreach (var abi_element in _abi_elements)
                {
                    Marshaler<T>.DisposeCache(abi_element);
                }
                return false;
            }
            
            public GCHandle _gchandle;
            public object[] _abi_elements;
        }

        public static Cache CreateCache(T[] array, Type abiType)
        {
            Cache cache = new Cache();
            try
            {
                var length = array.Length;
                var abi_array = Array.CreateInstance(abiType, length);
                cache._abi_elements = new object[length];
                for (int i = 0; i < length; i++)
                {
                    cache._abi_elements[i] = Marshaler<T>.CreateCache(array[i]);
                    abi_array.SetValue(Marshaler<T>.GetAbi(cache._abi_elements[i]), i);
                }
                cache._gchandle = GCHandle.Alloc(abi_array, GCHandleType.Pinned);
                return cache;
            }
            catch (Exception) when (cache.Dispose())
            {
                // Will never execute 
                return default;
            }
        }
        public static (IntPtr data, int length) GetAbi(object box)
        {
            var cache = (Cache)box;
            return (cache._gchandle.AddrOfPinnedObject(), ((Array)cache._gchandle.Target).Length);
        }
        public static unsafe T[] FromAbi(object box, Type abiType)
        {
            var abi = ((IntPtr data, int length))box;
            var array = new T[abi.length];
            var data = (byte*)abi.data.ToPointer();
            var abi_element_size = Marshal.SizeOf(abiType);
            for (int i = 0; i < abi.length; i++)
            {
                var abi_element = Marshal.PtrToStructure((IntPtr)data, abiType);
                array[i] = Marshaler<T>.FromAbi(abi_element);
                data += abi_element_size;
            }
            return array;
        }
        public static void DisposeCache(object box) => ((Cache)box).Dispose();
        public static void DisposeAbi(object box)
        {
#if !TEST_PASS_THRU
            var abi = ((IntPtr data, int length))box; 
            Marshal.FreeCoTaskMem(abi.data);
#endif
        }
    }

    struct MarshalStringArray
    {
        public struct Cache
        {
            public bool Dispose()
            {
                _gchandle.Dispose();
                foreach (var abi_string in _abi_strings)
                {
                    abi_string.Dispose();
                }
                return false;
            }
            
            public GCHandle _gchandle;
            public MarshalString.Cache[] _abi_strings;
        }

        public static Cache CreateCache(string[] array)
        {
            Cache cache = new Cache();
            try
            {
                var length = array.Length;
                var abi_array = new IntPtr[length];
                cache._abi_strings = new MarshalString.Cache[length];
                for (int i = 0; i < length; i++)
                {
                    cache._abi_strings[i] = MarshalString.CreateCache(array[i]);
                    abi_array[i] = MarshalString.GetAbi(cache._abi_strings[i]);
                };
                cache._gchandle = GCHandle.Alloc(abi_array, GCHandleType.Pinned);
                return cache;
            }
            catch (Exception) when (cache.Dispose())
            {
                // Will never execute 
                return default;
            }
        }
        public static (IntPtr data, int length) GetAbi(object box)
        {
            var cache = (Cache)box;
            return (cache._gchandle.AddrOfPinnedObject(), ((Array)cache._gchandle.Target).Length);
        }
        public static unsafe string[] FromAbi(object box)
        {
            var abi = ((IntPtr data, int length))box;
            string[] array = new string[abi.length];
            var data = (IntPtr*)abi.data.ToPointer();
            for (int i = 0; i < abi.length; i++)
            {
                array[i] = MarshalString.FromAbi(data[i]);
            }
            return array;
        }
        public static void DisposeCache(object box) => ((Cache)box).Dispose();
        public static void DisposeAbi(object box)
        {
#if !TEST_PASS_THRU
            var abi = ((IntPtr data, int length))box;
            Marshal.FreeCoTaskMem(abi.data);
#endif
        }
    }

    public class Marshaler<T>
    {
        static Marshaler()
        {
            Type type = typeof(T);

            // structs cannot contain arrays, and arrays must only ever appear as parameter types
            if (type.IsArray)
            {
                throw new InvalidOperationException("Arrays may not be marshaled generically.");
            }

            if (type.IsValueType)
            {
                // If type is blittable just pass through
                AbiType = Type.GetType($"{type.Namespace}.ABI.{type.Name}");
                if (AbiType == null)
                {
                    AbiType = type;
                    FromAbi = (object value) => (T)value;
                    CreateCache = (T value) => value;
                    GetAbi = (object box) => box;
                    DisposeCache = (object box) => {};
                    DisposeAbi = (object box) => { };
                    CreateCacheArray = (T[] array) => MarshalBlittableArray.CreateCache(array);
                    GetAbiArray = (object box) => MarshalBlittableArray.GetAbi(box);
                    FromAbiArray = (object box) => MarshalBlittableArray.FromAbi<T>(box);
                    DisposeCacheArray = (object box) => MarshalBlittableArray.DisposeCache(box);
                    DisposeAbiArray = (object box) => MarshalBlittableArray.DisposeAbi(box);
                }
                else // bind to ABI counterpart's FromAbi, ToAbi marshalers
                {
                    var CacheType = Type.GetType($"{type.Namespace}.ABI.{type.Name}+Cache");
                    CreateCache = BindCreateCache(AbiType);
                    GetAbi = BindGetAbi(AbiType, CacheType);
                    FromAbi = BindFromAbi(AbiType);
                    DisposeCache = BindDisposeCache(AbiType, CacheType);
                    DisposeAbi = (object box) => { };
                    CreateCacheArray = (T[] array) => MarshalNonBlittableArray<T>.CreateCache(array, AbiType);
                    GetAbiArray = (object box) => MarshalNonBlittableArray<T>.GetAbi(box);
                    FromAbiArray = (object box) => MarshalNonBlittableArray<T>.FromAbi(box, AbiType);
                    DisposeCacheArray = (object box) => MarshalNonBlittableArray<T>.DisposeCache(box);
                    DisposeAbiArray = (object box) => MarshalNonBlittableArray<T>.DisposeAbi(box);
                }
            }
            else if (type == typeof(String))
            {
                AbiType = typeof(IntPtr);
                FromAbi = (object value) => (T)(object)MarshalString.FromAbi((IntPtr)value);
                CreateCache = (T value) => Box.BoxValue(MarshalString.CreateCache((string)(object)value));
                GetAbi = (object box) => MarshalString.GetAbi(box);
                DisposeCache = (object box) => MarshalString.DisposeCache(box);
                DisposeAbi = (object box) => MarshalString.DisposeAbi(box);
                CreateCacheArray = (T[] array) => MarshalStringArray.CreateCache((string[])(object)array);
                GetAbiArray = (object box) => MarshalStringArray.GetAbi(box);
                FromAbiArray = (object box) => (T[])(object)MarshalStringArray.FromAbi(box);
                DisposeCacheArray = (object box) => MarshalStringArray.DisposeCache(box);
                DisposeAbiArray = (object box) => MarshalStringArray.DisposeAbi(box);
            }
            else // IInspectables (rcs, interfaces, delegates)
            {
                AbiType = typeof(IntPtr);
                FromAbi = (object value) => (T)value;
                //ToAbi = (T value) => (object)value;
            }
            RefAbiType = AbiType.MakeByRefType();
        }

        private static Func<T, object> BindCreateCache(Type AbiType)
        {
            var parms = new[] { Expression.Parameter(typeof(T), "arg") };
            return Expression.Lambda<Func<T, object>>(
                Expression.Convert(Expression.Call(AbiType.GetMethod("CreateCache"), parms),
                    typeof(object)), parms).Compile();
        }

        private static Func<object, object> BindGetAbi(Type AbiType, Type CacheType)
        {
            var parms = new[] { Expression.Parameter(typeof(object), "arg") };
            return Expression.Lambda<Func<object, object>>(
                Expression.Convert(Expression.Call(AbiType.GetMethod("GetAbi"),
                    new[] { Expression.Convert(parms[0], CacheType) }), 
                        typeof(object)), parms).Compile();
        }

        private static Func<object, T> BindFromAbi(Type AbiType)
        {
            var parms = new[] { Expression.Parameter(typeof(object), "arg") };
            return Expression.Lambda<Func<object, T>>(
                Expression.Call(AbiType.GetMethod("FromAbi"),
                    new[] { Expression.Convert(parms[0], AbiType) }), parms).Compile();
        }

        private static Action<object> BindDisposeCache(Type AbiType, Type CacheType)
        {
            var parms = new[] { Expression.Parameter(typeof(object), "arg") };
            return Expression.Lambda<Action<object>>(
                Expression.Call(AbiType.GetMethod("DisposeCache"),
                    new[] { Expression.Convert(parms[0], CacheType) }), parms).Compile();
        }

        public static readonly Type AbiType;
        public static readonly Type RefAbiType;
        public static readonly Func<object, T> FromAbi;
        public static readonly Func<T, object> CreateCache;
        public static readonly Func<object, object> GetAbi;
        public static readonly Action<object> DisposeCache;
        public static readonly Action<object> DisposeAbi;

        public static readonly Func<object, T[]> FromAbiArray;
        public static readonly Func<T[], object> CreateCacheArray;
        public static readonly Func<object, (IntPtr, int)> GetAbiArray;
        public static readonly Action<object> DisposeCacheArray;
        public static readonly Action<object> DisposeAbiArray;
    }

    class Generic<T>
    {
        static private readonly Marshaler<T> marshaler_T = new Marshaler<T>();

        static private readonly Type get_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.AbiType });
        static private readonly Type put_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.AbiType });
        static private readonly Type out_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.RefAbiType });
        static private readonly Type ref_type = Expression.GetDelegateType(new Type[] { typeof(void*), Marshaler<T>.RefAbiType });

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
            return Marshaler<T>.FromAbi(get_del.DynamicInvoke(@this));
        }

        public void Put(T value)
        {
            var cache = Marshaler<T>.CreateCache(value);
            put_del.DynamicInvoke(@this, Marshaler<T>.GetAbi(cache));
        }

        public void Out(out T value)
        {
            // Locals for pinning/conversion
            var parms = new object[] { @this, null }; 
            out_del.DynamicInvoke(parms);
            value = Marshaler<T>.FromAbi(parms[1]);
        }

        public void Ref(ref T value)
        {
            // Locals for pinning/conversion
            var cache = Marshaler<T>.CreateCache(value);
            var parms = new object[] { @this, Marshaler<T>.GetAbi(cache) };
            ref_del.DynamicInvoke(parms);
            value = Marshaler<T>.FromAbi(parms[1]);
        }
    }

    class GenericDelegate<T>
    {
        private unsafe delegate int GenericInvoke(IntPtr @this, [In] T arg);
        private unsafe delegate int NonGenericInvoke(IntPtr @this, [In] int arg);
        private static readonly Type GenericInvokeType = Expression.GetDelegateType(new Type[] { typeof(void*), typeof(T), typeof(int) });

        private static unsafe int InvokeGeneric(void* @this, [In] T arg)
        {
            var x = new IntPtr(@this); 
            return arg.GetHashCode();
        }

        static GenericInvoke FromAbiGeneric(IntPtr fp)
        {
            var invoke = Marshal.GetDelegateForFunctionPointer(fp, GenericInvokeType);
            return (IntPtr @this, T arg) => (int)invoke.DynamicInvoke(@this, arg);
        }

        static IntPtr ToAbiGeneric()
        {
            // if method delegate is generic, use GetDelegateType trick, else use GetFunctionPointerForDelegate
            var invoke = typeof(GenericDelegate<T>).GetMethod(nameof(InvokeGeneric), BindingFlags.Static | BindingFlags.NonPublic);
            return Marshal.GetFunctionPointerForDelegate(Delegate.CreateDelegate(GenericInvokeType, invoke));
        }

        private static unsafe int InvokeNonGeneric(IntPtr @this, [In] int arg)
        {
            return arg.GetHashCode();
        }

        static NonGenericInvoke FromAbiNonGeneric(IntPtr fp)
        {
            return Marshal.GetDelegateForFunctionPointer<NonGenericInvoke>(fp);
        }

        static IntPtr ToAbiNonGeneric()
        {
            return Marshal.GetFunctionPointerForDelegate(new NonGenericInvoke(InvokeNonGeneric));
        }

        public static unsafe void Test(T arg)
        {
            FromAbiGeneric(ToAbiGeneric()).Invoke(new IntPtr(42), arg);
            // Any generic nested delegate is generic, regardless of signature
            //FromAbiNonGeneric(ToAbiNonGeneric()).Invoke(new IntPtr(42), 42);
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

        public interface I1
        {
            public int MyProperty { get; }
        }

        public interface I2 : I1
        {
            public new int MyProperty { get; set; }
            //int I1.MyProperty => throw new NotImplementedException();
        }

        public interface I3
        {
            public int MyProperty { get; }
        }

        public class C : I1, I2, I3
        {
            private int _prop = 0;

            public int MyProperty => _prop;

            int I2.MyProperty { get => ((I1)this).MyProperty; set => _prop = value; }
        }

        static void TestPropertyInheritance()
        {
            C c = new C();
            int prop = ((I1)c).MyProperty;
            ((I2)c).MyProperty = 42;
            prop = ((I1)c).MyProperty;
            prop = ((I2)c).MyProperty;
            prop = ((I3)c).MyProperty;
            prop = c.MyProperty;
        }

        class Box<T>
        {
            public T _value;
        }

        static Box<int> CreateBox(int value)
        {
            return new Box<int> { _value = value };
        }

        static void ModifyBox(object box, int value)
        {
            ((Box<int>)box)._value = value;
        }

        static void TestBoxedValueModification()
        {
            var box = CreateBox(0);
            ModifyBox(box, 42);
            int boxed = box._value;
        }

        public struct TestExceptionRaiser
        {
            int _index;

            public TestExceptionRaiser(int index)
            {
                _index = index;
            }

            public void At(int index)
            {
                if (_index == index)
                    throw new Exception($"test exception at {index}");
            }
        };

        // ensure that non-generic marshaling is as efficient as possible (minimal heap allocations)
        public static void TestMarshalString(in string input, ref string byref, out string output, int throw_index = -1)
        {
            MarshalString.Cache input_in = default;
            MarshalString.Cache byref_in = default;
            IntPtr byref_out = default;
            IntPtr output_out = default;
            try
            {
                var raise = new TestExceptionRaiser(throw_index);

                raise.At(0);
                input_in = MarshalString.CreateCache(input);
                raise.At(1);
                byref_in = MarshalString.CreateCache(byref);
                raise.At(2);

                // trivial 'invoke': output = byref, byref = input
                output_out = MarshalString.GetAbi(byref_in);
                raise.At(3);
                byref_out = MarshalString.GetAbi(input_in);
                raise.At(4);

                output = MarshalString.FromAbi(output_out);
                raise.At(5);
                byref = MarshalString.FromAbi(byref_out);
                raise.At(6);
            }
            finally
            {
                MarshalString.DisposeCache(input_in);
                MarshalString.DisposeCache(byref_in);
                MarshalString.DisposeAbi(byref_out);
                MarshalString.DisposeAbi(output_out);
            }
        }

        public static void TestMarshalNonBlittable(in NonBlittable input, ref NonBlittable byref, out NonBlittable output, int throw_index = -1)
        {
            ABI.NonBlittable.Cache input_in = default;
            ABI.NonBlittable.Cache byref_in = default;
            ABI.NonBlittable byref_out = default;
            ABI.NonBlittable output_out = default;
            try
            {
                var raise = new TestExceptionRaiser(throw_index);

                raise.At(0);
                input_in = ABI.NonBlittable.CreateCache(input);
                raise.At(1);
                byref_in = ABI.NonBlittable.CreateCache(byref);
                raise.At(2);

                // trivial 'invoke': output = byref, byref = input
                output_out = ABI.NonBlittable.GetAbi(byref_in);
                raise.At(3);
                byref_out = ABI.NonBlittable.GetAbi(input_in);
                raise.At(4);

                output = ABI.NonBlittable.FromAbi(output_out);
                raise.At(5);
                byref = ABI.NonBlittable.FromAbi(byref_out);
                raise.At(6);
            }
            finally
            {
                ABI.NonBlittable.DisposeCache(input_in);
                ABI.NonBlittable.DisposeCache(byref_in);
            }
        }

        public static void TestMarshalGeneric<T>(in T input, ref T byref, out T output, int throw_index = -1)
        {
            object input_in = default;
            object byref_in = default;
            object byref_out = default;
            object output_out = default;
            try
            {
                var raise = new TestExceptionRaiser(throw_index);

                raise.At(0);
                input_in = Marshaler<T>.CreateCache(input);
                raise.At(1);
                byref_in = Marshaler<T>.CreateCache(byref);
                raise.At(2);

                // trivial 'invoke': output = byref, byref = input
                output_out = Marshaler<T>.GetAbi(byref_in);
                raise.At(3);
                byref_out = Marshaler<T>.GetAbi(input_in);
                raise.At(4);

                output = Marshaler<T>.FromAbi(output_out);
                raise.At(5);
                byref = Marshaler<T>.FromAbi(byref_out);
                raise.At(6);
            }
            finally 
            {
                Marshaler<T>.DisposeCache(input_in);
                Marshaler<T>.DisposeCache(byref_in);
                Marshaler<T>.DisposeAbi(byref_out);
                Marshaler<T>.DisposeAbi(output_out);
            }
        }

        public static void TestMarshalGenericArrays<T>(in T[] input, ref T[] byref, out T[] output, int throw_index = -1)
        {
            object input_in = default;
            object byref_in = default;
            object byref_out = default;
            object output_out = default;
            try
            {
                var raise = new TestExceptionRaiser(throw_index);

                raise.At(0);
                input_in = Marshaler<T>.CreateCacheArray(input);
                raise.At(1);
                byref_in = Marshaler<T>.CreateCacheArray(byref);
                raise.At(2);

                // trivial 'invoke': output = byref, byref = input
                output_out = Marshaler<T>.GetAbiArray(byref_in);
                raise.At(3);
                byref_out = Marshaler<T>.GetAbiArray(input_in);
                raise.At(4);

                output = Marshaler<T>.FromAbiArray(output_out);
                raise.At(5);
                byref = Marshaler<T>.FromAbiArray(byref_out);
                raise.At(6);
            }
            finally
            {
                Marshaler<T>.DisposeCacheArray(input_in);   
                Marshaler<T>.DisposeCacheArray(byref_in);
                Marshaler<T>.DisposeAbiArray(byref_out); 
                Marshaler<T>.DisposeAbiArray(output_out);
            }
        }

        static unsafe void Main(string[] args)
        {
            // test abi Dispose exception safety
            if (true)
            {
                int throw_index = -1; // 0..6 to throw

                int input_i = 1;
                int byref_i = 2;
                int output_i;
                try
                {
                    TestMarshalGeneric(input_i, ref byref_i, out output_i, throw_index);
                }
                catch (Exception e)
                {
                    System.Console.WriteLine(e);
                }

                string input_s = "hello";
                string byref_s = "world";
                string output_s;
                try
                {
                    //TestMarshalGeneric(input_s, ref byref_s, out output_s, throw_index);
                    TestMarshalString(input_s, ref byref_s, out output_s, throw_index);
                }
                catch (Exception e)
                {
                    System.Console.WriteLine(e);
                }
            }

            //TestBoxedValueModification();
            //TestPropertyInheritance();
            //var x = Marshaler<Blittable>.AbiType;
            //var y = Marshaler<NonBlittable>.RefAbiType;
            //GenericDelegate<int>.Test(42);

            // Int methods
            int in_int = 42;
            int out_int = 42;
            int ref_int = 1729;
            TestMarshalGeneric(in_int, ref ref_int, out out_int);
            //var gi = new Generic<int>(IntPtr.Zero,
            //    GetFP<GetInt>((IntPtr @this) => in_int),
            //    GetFP<PutInt>((IntPtr @this, int arg) => in_int = arg),
            //    GetFP<OutInt>((IntPtr @this, out int arg) => arg = out_int),
            //    GetFP<RefInt>((IntPtr @this, ref int arg) => arg = ref_int)
            //);
            //int i = gi.Get();
            //gi.Put(i);
            //i = 0;
            //gi.Out(out i);
            //Report("out int", i == out_int);
            //gi.Ref(ref i);
            //Report("ref int", i == ref_int);

            // String (IntPtr) methods 
            var in_string = "foo";
            var ref_string = "bar";
            string out_string = null;
            TestMarshalString(in_string, ref ref_string, out out_string);
            TestMarshalGeneric(in_string, ref ref_string, out out_string);

            //var gs = new Generic<string>(IntPtr.Zero,
            //    GetFP<GetPtr>((IntPtr @this) => MarshalString.ToAbi(in_string)),
            //    GetFP<PutPtr>((IntPtr @this, IntPtr arg) => in_string = MarshalString.FromAbi(arg)),
            //    GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalString.ToAbi(out_string)),
            //    GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalString.ToAbi(ref_string))
            //);
            //string s = gs.Get();
            //gs.Put(s);
            //s = "";
            //gs.Out(out s);
            //Report("out string", s == out_string);
            //gs.Ref(ref s);
            //Report("ref string", s == ref_string);

            // Blittable struct methods 
            var in_blittable = new Blittable { i = 42, d = 1729 };
            var ref_blittable = new Blittable { i = 1729, d = 42 };
            var out_blittable = new Blittable();
            TestMarshalGeneric(in_blittable, ref ref_blittable, out out_blittable);
            //var gb = new Generic<Blittable>(IntPtr.Zero,
            //    GetFP<GetBlittable>((IntPtr @this) => in_blittable),
            //    GetFP<PutBlittable>((IntPtr @this, Blittable arg) => in_blittable = arg),
            //    GetFP<OutBlittable>((IntPtr @this, out Blittable arg) => arg = out_blittable),
            //    GetFP<RefBlittable>((IntPtr @this, ref Blittable arg) => arg = ref_blittable)
            //);
            //Blittable b = gb.Get();
            //gb.Put(b);
            //b = new Blittable();
            //gb.Out(out b);
            //Report("out blittable", b.Equals(out_blittable));
            //gb.Ref(ref b);
            //Report("ref blittable", b.Equals(ref_blittable));

            // NonBlittable struct methods 
            var in_nonblittable = new NonBlittable { b = { i = 42, d = 1729 }, s1 = "foo", s2 = "bar" };
            var ref_nonblittable = new NonBlittable { b = { i = 1, d = 2 }, s1 = "hello", s2 = "world" };
            NonBlittable out_nonblittable = default;
            TestMarshalNonBlittable(in_nonblittable, ref ref_nonblittable, out out_nonblittable);
            TestMarshalGeneric(in_nonblittable, ref ref_nonblittable, out out_nonblittable);
            //var gn = new Generic<NonBlittable>(IntPtr.Zero,
            //    GetFP<GetNonBlittable>((IntPtr @this) => ABI.NonBlittable.ToAbi(in_nonblittable)),
            //    GetFP<PutNonBlittable>((IntPtr @this, ABI.NonBlittable arg) => in_nonblittable = ABI.NonBlittable.FromAbi(arg)),
            //    GetFP<OutNonBlittable>((IntPtr @this, out ABI.NonBlittable arg) => arg = ABI.NonBlittable.ToAbi(out_nonblittable)),
            //    GetFP<RefNonBlittable>((IntPtr @this, ref ABI.NonBlittable arg) => arg = ABI.NonBlittable.ToAbi(ref_nonblittable))
            //);
            //NonBlittable n = gn.Get();
            //gn.Put(n);
            //n = new NonBlittable();
            //gn.Out(out n);
            //Report("out nonblittable", n.Equals(out_nonblittable));
            //gn.Ref(ref n);
            //Report("ref nonblittable", n.Equals(ref_nonblittable));

            // Int array methods
            var in_ints = new[] { 42, 1729 };
            var ref_ints = new[] { 1, 2 };
            var out_ints = new int[2];
            TestMarshalGenericArrays(in_ints, ref ref_ints, out out_ints);
            //var gia = new Generic<int[]>(IntPtr.Zero,
            //    GetFP<GetPtr>((IntPtr @this) => MarshalArray<int[]>.ToAbi(in_ints)),
            //    GetFP<PutPtr>((IntPtr @this, IntPtr arg) => in_ints = MarshalArray<int[]>.FromAbi(arg)),
            //    GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<int[]>.ToAbi(out_ints)),
            //    GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<int[]>.ToAbi(ref_ints))
            //);
            //int[] ia = gia.Get();
            //gia.Put(ia);
            //ia = null;
            //gia.Out(out ia);
            //Report("out int array", ia.Length == out_ints.Length);
            //gia.Ref(ref ia);
            //Report("ref int array", ia.Length == ref_ints.Length);

            // String array (IntPtr) methods
            var in_strings = new[] { "foo", "bar" };
            var ref_strings = new[] { "hello", "world" };
            var out_strings = new string[2];
            TestMarshalGenericArrays(in_strings, ref ref_strings, out out_strings);
            //var gsa = new Generic<string[]>(IntPtr.Zero,
            //    GetFP<GetPtr>((IntPtr @this) => MarshalArray<string[]>.ToAbi(in_strings)),
            //    GetFP<PutPtr>((IntPtr @this, IntPtr arg) => in_strings = MarshalArray<string[]>.FromAbi(arg)),
            //    GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<string[]>.ToAbi(out_strings)),
            //    GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<string[]>.ToAbi(ref_strings))
            //);
            //string[] sa = gsa.Get();
            //gsa.Put(sa);
            //gsa.Out(out sa);
            //Report("out string array", sa.Length == out_strings.Length);
            //gsa.Ref(ref sa);
            //Report("ref string array", sa.Length == ref_strings.Length);

            // Blittable array (IntPtr) methods
            ref_blittable = new Blittable { i = 1729, d = 42 };
            Blittable[] in_blittables = new[] { in_blittable, in_blittable };
            Blittable[] ref_blittables = new[] { ref_blittable, ref_blittable };
            var out_blittables = new Blittable[2];
            TestMarshalGenericArrays(in_blittables, ref ref_blittables, out out_blittables);
            //var gba = new Generic<Blittable[]>(IntPtr.Zero,
            //    GetFP<GetPtr>((IntPtr @this) => MarshalArray<Blittable[]>.ToAbi(in_blittables)),
            //    GetFP<PutPtr>((IntPtr @this, IntPtr arg) => in_blittables = MarshalArray<Blittable[]>.FromAbi(arg)),
            //    GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<Blittable[]>.ToAbi(out_blittables)),
            //    GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<Blittable[]>.ToAbi(ref_blittables))
            //);
            //Blittable[] ba = gba.Get();
            //gba.Put(ba);
            //gba.Out(out ba);
            //Report("out blittable array", ba.Length == out_blittables.Length);
            //gba.Ref(ref ba);
            //Report("ref blittable array", ba.Length == ref_blittables.Length);

            // NonBlittable array (IntPtr) methods
            ref_nonblittable = new NonBlittable { b = { i = 1, d = 2 }, s1 = "hello", s2 = "world" };
            NonBlittable[] in_nonblittables = new[] { in_nonblittable, in_nonblittable };
            NonBlittable[] ref_nonblittables = new[] { ref_nonblittable, ref_nonblittable };
            var out_nonblittables = new NonBlittable[2];
            TestMarshalGenericArrays(in_nonblittables, ref ref_nonblittables, out out_nonblittables);
            //var gna = new Generic<NonBlittable[]>(IntPtr.Zero,
            //    GetFP<GetPtr>((IntPtr @this) => MarshalArray<NonBlittable[]>.ToAbi(in_nonblittables)),
            //    GetFP<PutPtr>((IntPtr @this, IntPtr arg) => in_nonblittables = MarshalArray<NonBlittable[]>.FromAbi(arg)),
            //    GetFP<OutPtr>((IntPtr @this, out IntPtr arg) => arg = MarshalArray<NonBlittable[]>.ToAbi(out_nonblittables)),
            //    GetFP<RefPtr>((IntPtr @this, ref IntPtr arg) => arg = MarshalArray<NonBlittable[]>.ToAbi(ref_nonblittables))
            //);
            //NonBlittable[] na = gna.Get();
            //gna.Put(na);
            //gna.Out(out na);
            //Report("out nonblittable array", na.Length == out_nonblittables.Length);
            //gna.Ref(ref na);
            //Report("ref nonblittable array", na.Length == ref_nonblittables.Length);
        }
    }
}
