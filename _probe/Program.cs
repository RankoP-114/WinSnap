using System;
using System.Linq;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

Console.WriteLine("SetDpi PMv2 -> " + Native.SetProcessDpiAwarenessContext((IntPtr)(-4)));

DXGI.CreateDXGIFactory1(out IDXGIFactory1 factory).CheckError();
factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
Console.WriteLine("adapter: " + adapter.Description1.Description);

var fls = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, fls,
    out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
Console.WriteLine("device FL=" + device.FeatureLevel);

adapter.EnumOutputs(0, out IDXGIOutput output).CheckError();
var out6 = output.QueryInterface<IDXGIOutput6>();
var d1 = out6.Description1;
Console.WriteLine($"output0: {d1.DeviceName} rect=({d1.DesktopCoordinates.Left},{d1.DesktopCoordinates.Top},{d1.DesktopCoordinates.Right},{d1.DesktopCoordinates.Bottom}) colorspace={d1.ColorSpace}");

var dup = out6.DuplicateOutput1(device, new[] { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm });
Console.WriteLine("DuplicateOutput1 OK. Desc.ModeDesc.Format=" + dup.Description.ModeDescription.Format);

// Acquire loop: keep acquiring/releasing until we get a frame with content
for (int attempt = 0; attempt < 60; attempt++)
{
    Result ar = dup.AcquireNextFrame(500, out OutduplFrameInfo fi, out IDXGIResource res);
    if (ar == Vortice.DXGI.ResultCode.WaitTimeout) { Console.WriteLine($"  [{attempt}] timeout"); continue; }
    if (ar.Failure) { Console.WriteLine($"  [{attempt}] Acquire fail 0x{ar.Code:X8}"); break; }

    var tex = res.QueryInterface<ID3D11Texture2D>();
    var sd = tex.Description;
    Console.WriteLine($"  [{attempt}] ACQUIRED fmt={sd.Format} {sd.Width}x{sd.Height} accumFrames={fi.AccumulatedFrames} lastPresent={fi.LastPresentTime} totalMeta={fi.TotalMetadataBufferSize} protected={fi.ProtectedContentMaskedOut}");

    // copy to staging + sample center pixel
    var stg = new Texture2DDescription { Width=sd.Width, Height=sd.Height, MipLevels=1, ArraySize=1, Format=sd.Format,
        SampleDescription=new SampleDescription(1,0), Usage=ResourceUsage.Staging, BindFlags=BindFlags.None,
        CPUAccessFlags=CpuAccessFlags.Read, MiscFlags=ResourceOptionFlags.None };
    var staging = device.CreateTexture2D(in stg);
    context.CopyResource(staging, tex);
    var mr = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource map);
    long nonzero = 0; double sum = 0;
    if (mr.Success && map.DataPointer != IntPtr.Zero)
    {
        unsafe {
            byte* bp = (byte*)map.DataPointer;
            int W=(int)sd.Width, H=(int)sd.Height;
            if (sd.Format == Format.R16G16B16A16_Float) {
                for (int y=0;y<H;y+=37){ ushort* r=(ushort*)(bp+(long)y*map.RowPitch); for(int x=0;x<W;x+=37){ float v=(float)BitConverter.UInt16BitsToHalf(r[x*4]); if(v!=0)nonzero++; sum+=v; } }
            } else {
                for (int y=0;y<H;y+=37){ byte* r=bp+(long)y*map.RowPitch; for(int x=0;x<W;x+=37){ byte v=r[x*4]; if(v!=0)nonzero++; sum+=v; } }
            }
        }
        context.Unmap(staging, 0);
    }
    Console.WriteLine($"        sampled nonzero={nonzero} sum={sum:F1}");
    staging.Dispose(); tex.Dispose(); res.Dispose();
    dup.ReleaseFrame();

    if (nonzero > 0) { Console.WriteLine("        >> got non-black content, stop."); break; }
    System.Threading.Thread.Sleep(100);
}

dup.Dispose(); out6.Dispose(); output.Dispose(); context.Dispose(); device.Dispose(); adapter.Dispose(); factory.Dispose();
Console.WriteLine("DONE");

static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
