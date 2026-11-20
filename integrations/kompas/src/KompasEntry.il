.assembly extern mscorlib {}
.assembly extern System.Windows.Forms
{
    .publickeytoken = (B7 7A 5C 56 19 34 E0 89)
    .ver 4:0:0:0
}
.assembly NormaCadEntry {}
.module NormaCadKompas.rtw
.corflags 0

// Native entry points for the KOMPAS procedure library loader.
.class private abstract sealed Entry
{
    .field private static initonly native int Name

    .method private static specialname rtspecialname void .cctor() cil managed
    {
        .maxstack 1
        ldstr "NormaCAD.PRO"
        call native int [mscorlib]System.Runtime.InteropServices.Marshal::StringToHGlobalUni(string)
        stsfld native int Entry::Name
        ret
    }

    .method public static int32 modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall) LibraryId() cil managed
    {
        .export [1] as LIBRARYID
        .maxstack 1
        ldc.i4 2000
        ret
    }

    .method public static void modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall) Run(int32 command) cil managed
    {
        .export [2] as LIBRARYENTRY
        .maxstack 6
        .try
        {
            call class [mscorlib]System.Reflection.Assembly [mscorlib]System.Reflection.Assembly::GetExecutingAssembly()
            callvirt instance string [mscorlib]System.Reflection.Assembly::get_Location()
            call string [mscorlib]System.IO.Path::GetDirectoryName(string)
            ldstr "NormaCadKompas.dll"
            call string [mscorlib]System.IO.Path::Combine(string, string)
            call class [mscorlib]System.Reflection.Assembly [mscorlib]System.Reflection.Assembly::LoadFrom(string)
            ldstr "NormaCad.Kompas.KompasCheck"
            callvirt instance class [mscorlib]System.Type [mscorlib]System.Reflection.Assembly::GetType(string)
            ldstr "RunNative"
            callvirt instance class [mscorlib]System.Reflection.MethodInfo [mscorlib]System.Type::GetMethod(string)
            ldnull
            ldc.i4.1
            newarr [mscorlib]System.Object
            dup
            ldc.i4.0
            ldarg.0
            box [mscorlib]System.Int32
            stelem.ref
            callvirt instance object [mscorlib]System.Reflection.MethodBase::Invoke(object, object[])
            pop
            leave.s Done
        }
        catch [mscorlib]System.Exception
        {
            callvirt instance string [mscorlib]System.Exception::get_Message()
            ldstr "NormaCAD.PRO"
            call valuetype [System.Windows.Forms]System.Windows.Forms.DialogResult [System.Windows.Forms]System.Windows.Forms.MessageBox::Show(string, string)
            pop
            leave.s Done
        }
        Done: ret
    }

    .method public static native int modopt([mscorlib]System.Runtime.CompilerServices.CallConvStdcall) LibraryName() cil managed
    {
        .export [3] as LIBRARYNAMEW
        .maxstack 1
        ldsfld native int Entry::Name
        ret
    }
}
