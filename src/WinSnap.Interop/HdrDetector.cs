using SharpGen.Runtime;
using Vortice;
using Vortice.DXGI;

namespace WinSnap.Interop;

/// <summary>
/// 通过 DXGI 枚举各显示器输出，读取 <c>DXGI_OUTPUT_DESC1.ColorSpace</c> 判断该屏当前是否处于 HDR 模式。
///
/// HDR 判定依据：<see cref="IDXGIOutput6"/>.<c>Description1</c>（<see cref="OutputDescription1"/>）的
/// <see cref="OutputDescription1.ColorSpace"/> == <see cref="ColorSpaceType.RgbFullG2084NoneP2020"/>
/// （即 ST.2084/PQ Rec.2020 全范围），对应 Windows「使用 HDR」开关已开启且驱动按 HDR wire format 输出。
/// SDR 屏通常报告 <c>RgbFullG22NoneP709</c>。
///
/// 坐标均为 <c>DesktopCoordinates</c>（物理像素，与虚拟桌面同一坐标系，可为负）。
/// </summary>
public static class HdrDetector
{
    /// <summary>
    /// 单个显示器输出的位置（物理像素，<c>DesktopCoordinates</c>）与 HDR 状态。
    /// </summary>
    public readonly record struct DisplayHdrInfo(int X, int Y, int Width, int Height, bool IsHdr)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    /// <summary>
    /// 枚举所有挂在桌面上的显示器输出及其 HDR 状态。
    /// 失败（无 DXGI、无适配器等）时返回空列表，绝不抛出。
    /// </summary>
    public static IReadOnlyList<DisplayHdrInfo> GetDisplays()
    {
        var result = new List<DisplayHdrInfo>();
        IDXGIFactory1? factory = null;
        try
        {
            // 1.1 工厂；失败直接返回空
            if (DXGI.CreateDXGIFactory1(out factory).Failure || factory is null)
                return result;

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
                    break; // DXGI_ERROR_NOT_FOUND：适配器枚举到头

                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        if (adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Failure || output is null)
                            break; // 该适配器输出枚举到头

                        try
                        {
                            AppendOutput(output, result);
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                }
                finally
                {
                    adapter.Dispose();
                }
            }
        }
        catch
        {
            // 任意意外（驱动异常等）→ 已收集到的尽量返回，不崩
        }
        finally
        {
            factory?.Dispose();
        }

        return result;
    }

    /// <summary>当前是否存在任意一块处于 HDR 模式的显示器。</summary>
    public static bool AnyHdrActive()
    {
        foreach (var d in GetDisplays())
            if (d.IsHdr) return true;
        return false;
    }

    private static void AppendOutput(IDXGIOutput output, List<DisplayHdrInfo> sink)
    {
        // QI 到 Output6 读 Desc1（含 ColorSpace）。老系统/虚拟显示器可能不支持 Output6。
        IDXGIOutput6? output6 = null;
        try
        {
            output6 = output.QueryInterface<IDXGIOutput6>();
        }
        catch
        {
            output6 = null;
        }

        if (output6 is not null)
        {
            try
            {
                OutputDescription1 desc1 = output6.Description1;
                RawRect r = desc1.DesktopCoordinates;
                bool isHdr = desc1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                if (desc1.AttachedToDesktop)
                {
                    sink.Add(new DisplayHdrInfo(
                        r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, isHdr));
                }
                return;
            }
            catch
            {
                // 落到下面用基础 OutputDescription
            }
            finally
            {
                output6.Dispose();
            }
        }

        // 退化路径：无 Output6 时只取几何，HDR 视为 false
        try
        {
            OutputDescription desc = output.Description;
            RawRect r = desc.DesktopCoordinates;
            if (desc.AttachedToDesktop)
            {
                sink.Add(new DisplayHdrInfo(
                    r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, false));
            }
        }
        catch
        {
            // 忽略该输出
        }
    }
}
