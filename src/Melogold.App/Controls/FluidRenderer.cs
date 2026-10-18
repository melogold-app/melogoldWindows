using System.Reflection;
using System.Runtime.InteropServices;
using Melogold.App.Services;
using Melogold.Core.Artwork;
using Melogold.Core.Visualizer;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Melogold.App.Controls;

/// <summary>
/// Шейдер фона «Сейчас играет» (tasks/0035) на Direct3D 11: <c>Shaders/fluid.hlsl</c> — перевод <c>fluid.frag</c> Linux один в один,
/// один треугольник на всю область. Обложка — текстура 256 × 256 с полной цепочкой мипов (считает <see cref="FluidTexture"/>),
/// трилинейная выборка, края — зеркальный повтор (<c>D3D11_TEXTURE_ADDRESS_MIRROR</c>, как <c>MIRRORED_REPEAT</c>), мип читается
/// <c>SampleLevel</c> (как <c>textureLod</c>). Swap chain — для <c>SwapChainPanel</c> (composition), размер в логических пикселях:
/// фон размытый, и рисовать его в физических не нужно. Любая ошибка (нет устройства, шейдер не собрался, устройство потеряно) —
/// <see cref="TryCreate"/> даёт null или <see cref="Render"/> — false, и <see cref="BackdropView"/> оставляет фон из tasks/0034.
/// </summary>
internal sealed class FluidRenderer : IDisposable
{
    private const string ShaderResource = "Melogold.Shaders.fluid.hlsl";

    /// <summary>Число float в буфере констант: 6 цветов + 2 строки сил капель + 5 строк скаляров = 13 строк по 4.</summary>
    private const int ConstantFloats = 13 * 4;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _pixel;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly float[] _packed = new float[ConstantFloats];
    private ID3D11RenderTargetView? _target;
    private ID3D11Texture2D? _cover;
    private ID3D11ShaderResourceView? _coverView;
    private int _width, _height;
    private float _luma = 0.3f;
    private int _paletteCount;
    private readonly float[] _palette = new float[6 * 4];

    private FluidRenderer(ID3D11Device device, ID3D11DeviceContext context, IDXGISwapChain1 swapChain, int width, int height,
        ID3D11VertexShader vertex, ID3D11PixelShader pixel, ID3D11Buffer constants, ID3D11SamplerState sampler, ID3D11RasterizerState rasterizer)
    {
        _device = device;
        _context = context;
        _swapChain = swapChain;
        _width = width;
        _height = height;
        _vertex = vertex;
        _pixel = pixel;
        _constants = constants;
        _sampler = sampler;
        _rasterizer = rasterizer;
        CreateTarget();
    }

    /// <summary>Указатель на swap chain, который надо отдать <c>SwapChainPanel</c> (<c>ISwapChainPanelNative.SetSwapChain</c>).</summary>
    public IntPtr SwapChainPointer => _swapChain.NativePointer;

    /// <summary>
    /// Устройство, swap chain и шейдеры. <paramref name="attach"/> отдаёт swap chain панели и говорит, получилось ли. null — на этой
    /// машине шейдер не работает (причина в журнале).
    /// </summary>
    public static FluidRenderer? TryCreate(int width, int height, Func<IntPtr, bool> attach)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGISwapChain1? swapChain = null;
        try
        {
            width = Math.Max(width, 1);
            height = Math.Max(height, 1);
            FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out _, out context);
            if (result.Failure || device is null || context is null)
            {
                Log.Warn($"Fluid backdrop: no Direct3D 11 device ({result})");
                device?.Dispose();
                context?.Dispose();
                return null;
            }

            using (var dxgiDevice = device.QueryInterface<IDXGIDevice1>())
            {
                dxgiDevice.MaximumFrameLatency = 1;
                using var adapter = dxgiDevice.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();
                var description = new SwapChainDescription1
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = SwapChainFlags.None,
                };
                swapChain = factory.CreateSwapChainForComposition(device, description);
            }

            var source = ReadShader();
            var vertexCode = Compile(source, "vs_main", "vs_4_0");
            var pixelCode = Compile(source, "ps_main", "ps_4_0");
            if (vertexCode is null || pixelCode is null)
            {
                swapChain.Dispose();
                context.Dispose();
                device.Dispose();
                return null;
            }

            var vertex = device.CreateVertexShader(vertexCode);
            var pixel = device.CreatePixelShader(pixelCode);
            var constants = device.CreateBuffer(new BufferDescription(ConstantFloats * sizeof(float), BindFlags.ConstantBuffer, ResourceUsage.Default));
            var sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Mirror,
                AddressV = TextureAddressMode.Mirror,
                AddressW = TextureAddressMode.Mirror,
                MipLODBias = 0,
                MaxAnisotropy = 1,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            });
            var rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));

            var renderer = new FluidRenderer(device, context, swapChain, width, height, vertex, pixel, constants, sampler, rasterizer);
            if (!attach(swapChain.NativePointer))
            {
                renderer.Dispose();
                return null;
            }
            Log.Info($"Fluid backdrop: Direct3D 11, {width}x{height}");
            return renderer;
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or COMException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("Fluid backdrop: Direct3D failed", e);
            swapChain?.Dispose();
            context?.Dispose();
            device?.Dispose();
            return null;
        }
    }

    private static string ReadShader()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ShaderResource)
            ?? throw new InvalidOperationException("Шейдер fluid.hlsl не вложен в сборку");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[]? Compile(string source, string entry, string profile)
    {
        var result = Compiler.Compile(source, null, null, entry, "fluid.hlsl", profile, ShaderFlags.OptimizationLevel3, EffectFlags.None, out var code, out var errors);
        if (result.Failure || code is null)
        {
            Log.Warn($"Fluid backdrop: shader {entry} did not compile: {errors?.AsString()}");
            errors?.Dispose();
            code?.Dispose();
            return null;
        }
        errors?.Dispose();
        var bytes = code.AsBytes();
        code.Dispose();
        return bytes;
    }

    /// <summary>Загрузить обложку в видеопамять: 9 уровней 256 → 1, яркость и палитра (до шести цветов).</summary>
    public bool SetCover(byte[] pixels, double luma, IReadOnlyList<ColorShare> shares)
    {
        try
        {
            var mips = FluidTexture.Mips(pixels);
            var handles = new GCHandle[mips.Length];
            var data = new SubresourceData[mips.Length];
            try
            {
                for (var level = 0; level < mips.Length; level++)
                {
                    handles[level] = GCHandle.Alloc(mips[level], GCHandleType.Pinned);
                    data[level] = new SubresourceData(handles[level].AddrOfPinnedObject(), (uint)((FluidTexture.Side >> level) * 4));
                }
                var texture = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = FluidTexture.Side,
                    Height = FluidTexture.Side,
                    MipLevels = FluidTexture.Levels,
                    ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                }, data);
                var view = _device.CreateShaderResourceView(texture);
                _coverView?.Dispose();
                _cover?.Dispose();
                _cover = texture;
                _coverView = view;
            }
            finally
            {
                foreach (var handle in handles)
                {
                    if (handle.IsAllocated) handle.Free();
                }
            }
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or COMException or ArgumentException)
        {
            Log.Warn("Fluid backdrop: cover texture failed", e);
            return false;
        }

        _luma = (float)luma;
        Array.Clear(_palette);
        _paletteCount = Math.Min(shares.Count, 6);
        for (var i = 0; i < _paletteCount; i++)
        {
            _palette[i * 4] = ((shares[i].Rgb >> 16) & 0xFF) / 255f;
            _palette[i * 4 + 1] = ((shares[i].Rgb >> 8) & 0xFF) / 255f;
            _palette[i * 4 + 2] = (shares[i].Rgb & 0xFF) / 255f;
            _palette[i * 4 + 3] = (float)shares[i].Share;
        }
        return true;
    }

    public bool HasCover => _coverView is not null;

    private void CreateTarget()
    {
        using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _target = _device.CreateRenderTargetView(buffer);
    }

    /// <summary>
    /// Нарисовать кадр размером <paramref name="width"/> × <paramref name="height"/> и показать. false — устройство потеряно или
    /// рисовать нечем: фон надо заменить запасным.
    /// </summary>
    public bool Render(int width, int height, FluidMotion motion, FluidAnalysis analysis)
    {
        if (_coverView is null) return false;
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        try
        {
            if (width != _width || height != _height)
            {
                _target?.Dispose();
                _target = null;
                _context.ClearState();
                var resize = _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
                if (resize.Failure) return Failed($"resize {resize}");
                _width = width;
                _height = height;
                CreateTarget();
            }

            Pack(motion, analysis);
            _context.UpdateSubresource(_packed, _constants);
            _context.OMSetRenderTargets(_target!);
            _context.RSSetViewport(0, 0, width, height);
            _context.RSSetState(_rasterizer);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.IASetInputLayout(null);
            _context.VSSetShader(_vertex);
            _context.PSSetShader(_pixel);
            _context.PSSetConstantBuffer(0, _constants);
            _context.PSSetShaderResource(0, _coverView);
            _context.PSSetSampler(0, _sampler);
            _context.Draw(3, 0);

            var present = _swapChain.Present(0, PresentFlags.None);
            if (present.Failure) return Failed($"present {present}");
            return true;
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or COMException or InvalidOperationException)
        {
            Log.Warn("Fluid backdrop: frame failed", e);
            return false;
        }
    }

    private bool Failed(string what)
    {
        Log.Warn($"Fluid backdrop: {what}, device removed reason {_device.DeviceRemovedReason}");
        return false;
    }

    /// <summary>Константы в порядке <c>cbuffer Constants</c> из fluid.hlsl.</summary>
    private void Pack(FluidMotion motion, FluidAnalysis analysis)
    {
        var p = _packed;
        Array.Copy(_palette, 0, p, 0, 24);
        for (var i = 0; i < FluidMotion.Blobs; i++) p[24 + i] = motion.Blob[i];
        p[30] = 0;
        p[31] = 0;
        p[32] = _width;
        p[33] = _height;
        p[34] = 0;
        p[35] = 0;
        p[36] = motion.Liquid;
        p[37] = motion.LiquidFlow;
        p[38] = motion.LavaFlow;
        p[39] = motion.Pulse;
        p[40] = FluidLook.LavaMix;
        p[41] = analysis.Loud;
        p[42] = _luma;
        p[43] = FluidLook.LiquidBase;
        p[44] = FluidLook.BlobScale;
        p[45] = FluidLook.Zoom;
        p[46] = FluidLook.LodBase;
        p[47] = FluidLook.LodPulse;
        p[48] = FluidLook.Bright;
        p[49] = FluidLook.Saturation;
        p[50] = _paletteCount;
        p[51] = 0;
    }

    public void Dispose()
    {
        try
        {
            _context.ClearState();
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or COMException)
        {
            // Устройство уже потеряно
        }
        _coverView?.Dispose();
        _cover?.Dispose();
        _target?.Dispose();
        _rasterizer.Dispose();
        _sampler.Dispose();
        _constants.Dispose();
        _pixel.Dispose();
        _vertex.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
