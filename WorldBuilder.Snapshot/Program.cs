using System;
using System.Globalization;
using System.IO;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Chorizite.OpenGLSDLBackend;

using DatReaderWriter.Options;

using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Snapshot {
    /// <summary>
    /// Headless dungeon-interior renderer. Drives the Dungeon editor's own scene
    /// (DungeonScene -> EnvCellManager + StaticObjectManager: real cell geometry, surfaces, baked decor,
    /// portal visibility) against an invisible GL window and an offscreen FBO - no Avalonia, no project,
    /// reads retail dats directly.
    ///
    /// Usage:
    ///   WorldBuilder.Snapshot --landblock=0x0066 --png=out.png
    ///       [--campos=x,y,z --lookat=x,y,z]   camera in landblock-frame coords (same space as
    ///                                          landblock_instance origins / @loc); omit for auto-framing
    ///       [--width=1280] [--height=800] [--frames=240] [--dats=C:\ACE\Dats]
    /// </summary>
    internal static class Program {
        private static int Main(string[] args) {
            uint? landblock = null;
            string? pngPath = null;
            Vector3? camPos = null, lookAt = null;
            var width = 1280;
            var height = 800;
            var frames = 240;
            var datPath = @"C:\ACE\Dats";

            foreach (var arg in args) {
                if (!arg.StartsWith("--")) continue;
                var eq = arg.IndexOf('=');
                if (eq < 0) continue;
                var key = arg[2..eq].ToLowerInvariant();
                var value = arg[(eq + 1)..];

                switch (key) {
                    case "landblock": landblock = Convert.ToUInt32(value.Trim(), 16); break;
                    case "png": pngPath = value; break;
                    case "campos": camPos = ParseVector3(value); break;
                    case "lookat": lookAt = ParseVector3(value); break;
                    case "width": width = int.Parse(value); break;
                    case "height": height = int.Parse(value); break;
                    case "frames": frames = int.Parse(value); break;
                    case "dats": datPath = value; break;
                }
            }

            if (landblock == null || pngPath == null) {
                Console.WriteLine("Usage: WorldBuilder.Snapshot --landblock=0x0066 --png=out.png [--campos=x,y,z --lookat=x,y,z] [--width=1280] [--height=800] [--frames=240] [--dats=C:\\ACE\\Dats]");
                return 1;
            }

            // ---- invisible GL context ------------------------------------------------------------
            // The scene shaders are "#version 300 es" (written for Avalonia's ANGLE ES context), so try a
            // real ES 3.0 context first; desktop GL is the fallback (most Windows drivers accept ES shaders
            // there via ARB_ES3_compatibility).
            var window = CreateHiddenWindow(width, height, out var apiUsed);

            var gl = window.CreateOpenGL();
            Console.WriteLine($"[snapshot] GL context: {apiUsed}, renderer: {gl.GetStringS(StringName.Renderer)}");

            using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
            var renderer = new OpenGLRenderer(gl, loggerFactory.CreateLogger("Snapshot"), null!, width, height);

            // ---- scene ---------------------------------------------------------------------------
            using var dats = new DefaultDatReaderWriter(datPath, DatAccessType.Read);
            var settings = new WorldBuilderSettings();

            var scene = new DungeonScene(dats, settings) {
                ShowGrid = false,
                ShowConnectionLines = false,
                ShowPortalIndicators = false,
            };
            scene.InitGpu(renderer);

            if (!scene.LoadLandblock((ushort)landblock.Value)) {
                Console.WriteLine($"0x{landblock.Value:X4} has no dungeon cells - nothing to render.");
                return 1;
            }

            scene.Camera.ScreenSize = new Vector2(width, height);

            // ---- offscreen FBO ---------------------------------------------------------------------
            var fbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

            var colorTex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, colorTex);
            unsafe {
                gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            }
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);

            var depthRb = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depthRb);

            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete) {
                Console.WriteLine("Offscreen framebuffer incomplete - cannot render.");
                return 1;
            }

            // ---- render loop -----------------------------------------------------------------------
            // DungeonScene.Render pumps the EnvCell upload queue and static-object model warmups a few
            // items per frame, so cells/decor stream in over the first dozens of frames. Warm up first,
            // then place the camera from the loaded cells, then render the remaining frames.
            var aspect = (float)width / height;
            var warmupFrames = Math.Max(60, frames / 3);

            for (var frame = 0; frame < warmupFrames; frame++) {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                scene.Render(aspect);
            }

            var cells = scene.EnvCells?.GetLoadedCellsForLandblock((ushort)landblock.Value);
            if (cells == null || cells.Count == 0) {
                Console.WriteLine("No cells loaded after warmup - cannot place camera.");
                return 1;
            }

            // The scene positions cells in world space (landblock offset + a dungeon Z bump). Probe the
            // exact local->world offset from a loaded cell vs its dat-side origin, so --campos/--lookat
            // stay in landblock-frame coordinates - the same space as landblock_instance origins, portal
            // destinations, and @loc output.
            var probe = cells[0];
            if (!dats.TryGet<DatReaderWriter.DBObjs.EnvCell>(probe.CellId, out var probeDatCell)) {
                Console.WriteLine($"Failed to re-read probe cell 0x{probe.CellId:X8} from the dats.");
                return 1;
            }
            var worldOffset = probe.WorldPosition - probeDatCell.Position.Origin;

            Vector3 eye, target;
            if (camPos != null && lookAt != null) {
                eye = camPos.Value + worldOffset;
                target = lookAt.Value + worldOffset;
            }
            else {
                // auto-frame from loaded cell origins: stand inside, pulled back from the center along the
                // longest horizontal axis (bbox corners are often outside octagon/cross-shaped rooms),
                // slightly above the lowest floor, looking across at the center
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var cell in cells) {
                    min = Vector3.Min(min, cell.WorldPosition);
                    max = Vector3.Max(max, cell.WorldPosition);
                }
                var boundsCenter = (min + max) / 2;
                var extent = max - min;

                var offset = extent.X >= extent.Y
                    ? new Vector3(extent.X * 0.36f, 0, 0)
                    : new Vector3(0, extent.Y * 0.36f, 0);

                eye = new Vector3(boundsCenter.X + offset.X, boundsCenter.Y + offset.Y, min.Z + Math.Min(extent.Z * 0.35f, 3.5f) + 1.8f);
                target = new Vector3(boundsCenter.X, boundsCenter.Y, min.Z + 2f);
            }

            scene.Camera.SetPosition(eye);
            scene.Camera.LookAt(target);

            for (var frame = warmupFrames; frame < frames; frame++) {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                scene.Render(aspect);
            }

            // ---- read back + save -------------------------------------------------------------------
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);

            var pixels = new byte[width * height * 4];
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, (Span<byte>)pixels);

            // GL reads bottom-up; also force alpha opaque
            using var image = new Image<Rgba32>(width, height);
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var i = ((height - 1 - y) * width + x) * 4;
                    image[x, y] = new Rgba32(pixels[i], pixels[i + 1], pixels[i + 2], 255);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath)) ?? ".");
            image.SaveAsPng(pngPath);

            var center = scene.GetDungeonCenter();
            Console.WriteLine($"[snapshot] 0x{landblock.Value:X4} -> {pngPath}");
            if (center != null)
                Console.WriteLine($"[snapshot] dungeon center (landblock frame): {center.Value.X:0.#},{center.Value.Y:0.#},{center.Value.Z:0.#}");
            Console.WriteLine($"[snapshot] camera: {(camPos != null ? $"local pos {camPos.Value} look {lookAt!.Value}" : "auto (inside loaded-cell bounds)")}");

            window.Close();
            return 0;
        }

        private static IWindow CreateHiddenWindow(int width, int height, out string apiUsed) {
            var baseOptions = WindowOptions.Default with {
                Size = new Vector2D<int>(width, height),
                Title = "WorldBuilder.Snapshot",
                IsVisible = false,
                WindowBorder = WindowBorder.Hidden,
                VSync = false,
            };

            try {
                var es = Window.Create(baseOptions with {
                    API = new GraphicsAPI(ContextAPI.OpenGLES, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 0)),
                });
                es.Initialize();
                apiUsed = "OpenGL ES 3.0";
                return es;
            }
            catch (Exception ex) {
                Console.WriteLine($"[snapshot] ES 3.0 context unavailable ({ex.Message}) - falling back to desktop GL (ES shaders via ARB_ES3_compatibility).");
            }

            var desktop = Window.Create(baseOptions with {
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Compatability, ContextFlags.Default, new APIVersion(3, 3)),
            });
            desktop.Initialize();
            apiUsed = "desktop OpenGL 3.3 compatibility";
            return desktop;
        }

        private static Vector3 ParseVector3(string value) {
            var parts = value.Split(',');
            return new Vector3(
                float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture));
        }
    }
}
