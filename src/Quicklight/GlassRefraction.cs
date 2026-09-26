using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Quicklight.Core;

namespace Quicklight;

/// <summary>
/// Refraction for the selection glass, the pixel-shader counterpart of the SVG filter chain used for liquid glass on
/// the web: feDisplacementMap (here the displacement is computed from the pane's rounded shape instead of read from a
/// gradient image) and feColorMatrix + feOffset + feBlend (each color channel displaced a little differently).
/// The input is the backdrop behind the pane plus <see cref="Margin"/> on every side, so the rim can bend in what lies
/// just outside it. The shader is compiled once at startup with Windows' own d3dcompiler_47.dll.
/// </summary>
public sealed class GlassRefraction : ShaderEffect
{
    public const double Margin = 16; // px of backdrop around the pane in the input

    const string Hlsl = """
        sampler2D input : register(s0);
        float width : register(c0);
        float height : register(c1);
        float radius : register(c2);
        float bezel : register(c3);    // px from the edge over which the surface curves
        float margin : register(c4);
        float zoom : register(c5);     // magnification in the flat middle
        float bend : register(c6);     // px the rim pulls in from outside
        float dispersion : register(c7);

        // Signed distance to the rounded rectangle (negative inside).
        float box(float2 p, float2 hs)
        {
            float2 q = abs(p) - hs + radius;
            return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
        }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float2 size = float2(width, height);
            float2 hs = size * 0.5;
            float2 p = uv * size - hs;
            float d = box(p, hs);
            float2 n = float2(box(p + float2(0.5, 0), hs) - box(p - float2(0.5, 0), hs),
                              box(p + float2(0, 0.5), hs) - box(p - float2(0, 0.5), hs));
            n = n / max(length(n), 1e-4);

            // Displacement map: none in the middle, rising steeply towards the rim like a lens edge.
            float t = saturate(1 + d / bezel);
            float2 shift = n * (t * t * t * bend);
            float2 base = p / zoom + hs + margin;
            float2 total = size + 2 * margin;

            float4 c;
            c.r = tex2D(input, (base + shift * (1 + dispersion)) / total).r;
            c.g = tex2D(input, (base + shift) / total).g;
            c.b = tex2D(input, (base + shift * (1 - dispersion)) / total).b;
            c.a = tex2D(input, (base + shift) / total).a; // no backdrop (solid mode): nothing
            return c * saturate(0.5 - d); // the pane's rounded shape, antialiased (premultiplied alpha)
        }
        """;

    static readonly Lazy<PixelShader?> Shader = new(Compile);

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassRefraction), 0);
    public static readonly DependencyProperty WidthProperty = Constant("Width", 0, 100);
    public static readonly DependencyProperty HeightProperty = Constant("Height", 1, 40);
    static readonly DependencyProperty RadiusProperty = Constant("Radius", 2, 12);
    static readonly DependencyProperty BezelProperty = Constant("Bezel", 3, 12);
    static readonly DependencyProperty MarginProperty = Constant("Margin", 4, Margin);
    static readonly DependencyProperty ZoomProperty = Constant("Zoom", 5, 1.1);
    static readonly DependencyProperty BendProperty = Constant("Bend", 6, 12);
    static readonly DependencyProperty DispersionProperty = Constant("Dispersion", 7, 0.35);

    static DependencyProperty Constant(string name, int register, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(GlassRefraction), new UIPropertyMetadata(value, PixelShaderConstantCallback(register)));

    public double Width { get => (double)GetValue(WidthProperty); set => SetValue(WidthProperty, value); }
    public double Height { get => (double)GetValue(HeightProperty); set => SetValue(HeightProperty, value); }

    GlassRefraction(PixelShader shader)
    {
        PixelShader = shader;
        foreach (var p in new[] { InputProperty, WidthProperty, HeightProperty, RadiusProperty, BezelProperty, MarginProperty, ZoomProperty, BendProperty, DispersionProperty })
            UpdateShaderValue(p);
    }

    /// <summary>Null when the shader could not be compiled; the glass then shows a plain magnified backdrop.</summary>
    public static GlassRefraction? TryCreate() => Shader.Value is { } s ? new GlassRefraction(s) : null;

    static unsafe PixelShader? Compile()
    {
        try
        {
            var src = Encoding.ASCII.GetBytes(Hlsl);
            int hr = D3DCompile(src, src.Length, "glass", 0, 0, "main", "ps_3_0", 0, 0, out var code, out var errors);
            try
            {
                if (hr < 0)
                {
                    string message = errors == 0 ? $"0x{hr:X8}" : Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors));
                    Log.Error("glass shader compile failed: " + message);
                    return null;
                }
                var bytes = new byte[BlobSize(code)];
                Marshal.Copy(BlobPointer(code), bytes, 0, bytes.Length);
                var shader = new PixelShader();
                shader.SetStreamSource(new MemoryStream(bytes));
                shader.Freeze();
                return shader;
            }
            finally
            {
                Release(code);
                Release(errors);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            Log.Error("glass shader unavailable", ex);
            return null;
        }
    }

    // ID3DBlob, called through its vtable: IUnknown (0-2), GetBufferPointer (3), GetBufferSize (4).
    static unsafe nint Slot(nint blob, int i) => (*(nint**)blob)[i];
    static unsafe nint BlobPointer(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(blob, 3))(blob);
    static unsafe nint BlobSize(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(blob, 4))(blob);
    static unsafe void Release(nint blob) { if (blob != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(blob, 2))(blob); }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    static extern int D3DCompile(byte[] src, nint size, string name, nint defines, nint include, string entry, string target,
        uint flags1, uint flags2, out nint code, out nint errors);
}
