using System;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using Chorizite.OpenGLSDLBackend;

using DatReaderWriter.Options;

using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Snapshot {
    /// <summary>
    /// Headless renderer for both halves of the world.
    ///
    /// DUNGEON INTERIORS (default): drives the Dungeon editor's own scene (DungeonScene ->
    /// EnvCellManager + StaticObjectManager: real cell geometry, surfaces, baked decor, portal
    /// visibility) against an invisible GL window and an offscreen FBO - no Avalonia, no project,
    /// reads retail dats directly.
    ///
    /// OUTDOOR TERRAIN (--view=landscape): drives the Landscape editor's GameScene the same way -
    /// real terrain geometry, blended surface textures, scenery and building statics. Used for siting
    /// "outdoor dungeons" (see the ACE repo's Docs/Outdoor-Dungeons/DESIGN.md), where surfacemap answers
    /// "will this ground work" and this answers "what does the place look like".
    ///
    /// Unlike DungeonScene, the landscape scene hangs off a TerrainSystem, which wants a Project and a
    /// DocumentManager. Both are built here in-memory against a scratch directory: no dats are copied,
    /// no project is saved, and an empty document set means the terrain resolves straight from the dats
    /// (TerrainDocument seeds its base cache from them), which is exactly the retail world we want.
    ///
    /// Usage:
    ///   WorldBuilder.Snapshot --landblock=0x0066 --png=out.png
    ///       [--view=overview|landscape]
    ///       [--campos=x,y,z --lookat=x,y,z]   camera in landblock-frame coords (same space as
    ///                                          landblock_instance origins / @loc); omit for auto-framing
    ///       [--elevation=38] [--heading=S|N|W|E]   landscape framing controls
    ///       [--width=1280] [--height=800] [--frames=240] [--dats=C:\ACE\Dats]
    /// </summary>
    internal static class Program {
        private static int Main(string[] args) {
            uint? landblock = null;
            string? pngPath = null;
            Vector3? camPos = null, lookAt = null;
            var width = 1280;
            var height = 800;
            var frames = 240;   // landscape mode treats this as a CAP and stops once chunks settle
            var datPath = @"C:\ACE\Dats";
            var view = "auto";      // auto (inside) | overview (fitted tilted top view with roof cut) | landscape
            float? cutZ = null;     // landblock-frame Z; geometry above it is sliced off
            var camElevation = 38f; // landscape: degrees above horizontal
            var heading = "S";      // landscape: which side the camera sits on

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
                    case "view": view = value.ToLowerInvariant(); break;
                    case "cutz": cutZ = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "elevation": camElevation = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "heading": heading = value.ToUpperInvariant(); break;
                }
            }

            if (landblock == null || pngPath == null) {
                Console.WriteLine("Usage: WorldBuilder.Snapshot --landblock=0x0066 --png=out.png [--view=overview|landscape] [--cutz=<localZ>] [--campos=x,y,z --lookat=x,y,z] [--elevation=38] [--heading=S|N|W|E] [--width=1280] [--height=800] [--frames=240] [--dats=C:\\ACE\\Dats]");
                return 1;
            }

            if (view == "landscape")
                return RenderLandscape(landblock.Value, pngPath, camPos, lookAt, width, height,
                    args.Any(a => a.StartsWith("--frames=")) ? frames : 1800, datPath, camElevation, heading);

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

            if (cutZ != null)
                scene.SectionCutWorldZ = cutZ.Value + worldOffset.Z;

            Vector3 eye, target;
            if (view == "overview") {
                // dollhouse view: whole dungeon in frame from a tilted top angle, roof sliced off by the
                // shader section cut so the interior reads in 3D against the void
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var cell in cells) {
                    min = Vector3.Min(min, cell.WorldPosition);
                    max = Vector3.Max(max, cell.WorldPosition);
                }
                var mid = (min + max) / 2;
                var extent = max - min;

                // default cut low enough that mid-level floors (galleries, walkways) don't lid the view;
                // multi-level dungeons often still want an explicit --cutz below/above a specific floor
                if (cutZ == null)
                    scene.SectionCutWorldZ = min.Z + Math.Max(extent.Z * 0.45f, 4f);

                // fit the layout's bounding circle to the vertical FOV, with a small margin - refined
                // below by measuring the actual rendered content
                var radius = MathF.Sqrt(extent.X * extent.X + extent.Y * extent.Y) / 2f + 8f;
                var fovRadians = (float)(settings.Landscape.Camera.FieldOfView * Math.PI / 180.0);
                var distance = Math.Max(radius / MathF.Tan(fovRadians / 2f), 25f);

                // ~38 degrees above horizontal from the south, north at the top of frame. The overview
                // path bypasses PerspectiveCamera entirely (its worldUp = -Z left-handed convention reads
                // upside-down in top views) and injects a conventional right-handed view-projection.
                const float elevation = 38f * MathF.PI / 180f;
                eye = mid + new Vector3(0, -distance * MathF.Cos(elevation), distance * MathF.Sin(elevation));
                target = new Vector3(mid.X, mid.Y, min.Z);

                scene.OverrideViewProjection =
                    Matrix4x4.CreateLookAt(eye, target, Vector3.UnitZ) *
                    Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, aspect, 1f, 4000f);
            }
            else if (camPos != null && lookAt != null) {
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

            var pixels = CaptureFrame(gl, fbo, width, height);

            // overview: the FOV-based first fit is approximate (and perspective response to distance is
            // nonlinear up close) - iteratively measure the rendered content's pixel bounding box and
            // re-fit until the widest edge nearly reaches the frame. Zoom-in is clamped per pass;
            // touching a frame edge means overflow, so back out.
            if (view == "overview") {
                var currentDistance = (eye - target).Length();
                var direction = Vector3.Normalize(eye - target);
                var fovRadians = (float)(settings.Landscape.Camera.FieldOfView * Math.PI / 180.0);

                for (var pass = 0; pass < 7; pass++) {
                    var content = MeasureContent(pixels, width, height);
                    if (content.Width <= 0) break;

                    float factor;
                    if (content.TouchesEdge)
                        factor = 1.25f;                                     // overflow - back out
                    else {
                        var fill = Math.Max(content.Width / (width * 0.85f), content.Height / (height * 0.85f));
                        if (fill > 0.88f) break;                            // fitted, nothing clipped
                        factor = Math.Max(fill, 0.7f);                      // clamped zoom-in
                    }

                    currentDistance *= factor;
                    var newEye = target + direction * currentDistance;
                    scene.Camera.SetPosition(newEye);   // keep visibility culling in sync
                    scene.OverrideViewProjection =
                        Matrix4x4.CreateLookAt(newEye, target, Vector3.UnitZ) *
                        Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, aspect, 1f, 4000f);

                    for (var frame = 0; frame < 20; frame++) {
                        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                        scene.Render(aspect);
                    }
                    pixels = CaptureFrame(gl, fbo, width, height);
                }
            }

            // GL reads bottom-up; force alpha opaque
            using var image = new Image<Rgba32>(width, height);
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var i = ((height - 1 - y) * width + x) * 4;
                    image[x, y] = new Rgba32(pixels[i], pixels[i + 1], pixels[i + 2], 255);
                }
            }

            // overview: crop to the content plus a slim margin so the saved image carries almost no void
            if (view == "overview") {
                var box = MeasureContentBox(pixels, width, height);
                if (box != null) {
                    const int margin = 24;
                    var (minPx, minPy, maxPx, maxPy) = box.Value;
                    // pixel bbox was measured on the raw (bottom-up) buffer - flip its Y for image space
                    var top = Math.Max(height - 1 - maxPy - margin, 0);
                    var bottom = Math.Min(height - 1 - minPy + margin, height - 1);
                    var left = Math.Max(minPx - margin, 0);
                    var right = Math.Min(maxPx + margin, width - 1);
                    image.Mutate(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(left, top, right - left + 1, bottom - top + 1)));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath)) ?? ".");
            image.SaveAsPng(pngPath);

            var center = scene.GetDungeonCenter();
            Console.WriteLine($"[snapshot] 0x{landblock.Value:X4} -> {pngPath}");
            if (center != null)
                Console.WriteLine($"[snapshot] dungeon center (landblock frame): {center.Value.X:0.#},{center.Value.Y:0.#},{center.Value.Z:0.#}");
            var cameraLabel = view == "overview" ? "overview (fitted tilted top view)"
                : camPos != null ? $"local pos {camPos.Value} look {lookAt!.Value}"
                : "auto (inside loaded-cell bounds)";
            Console.WriteLine($"[snapshot] camera: {cameraLabel}{(scene.SectionCutWorldZ != null ? $", section cut at local z {scene.SectionCutWorldZ.Value - worldOffset.Z:0.#}" : "")}");

            window.Close();
            return 0;
        }

        /// <summary>
        /// Outdoor terrain render, driving the Landscape editor's GameScene headlessly.
        ///
        /// The scene needs a TerrainSystem, which needs a Project and a DocumentManager. Both are built
        /// in-memory here against a scratch directory - no dats copied, no project written. An empty
        /// document set is exactly what we want: TerrainDocument seeds its base terrain cache straight
        /// from the dats, so what renders is the retail world rather than someone's edits.
        ///
        /// That base cache is a scan of all 255x255 landblocks, so it is cached to disk on first run and
        /// reused after - the scratch directory is deliberately stable, not a fresh temp folder per run.
        /// </summary>
        private static int RenderLandscape(uint landblock, string pngPath, Vector3? camPos, Vector3? lookAt,
                                           int width, int height, int frames, string datPath,
                                           float elevationDegrees, string heading) {
            var window = CreateHiddenWindow(width, height, out var apiUsed);
            var gl = window.CreateOpenGL();
            Console.WriteLine($"[snapshot] GL context: {apiUsed}, renderer: {gl.GetStringS(StringName.Renderer)}");

            using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
            var renderer = new OpenGLRenderer(gl, loggerFactory.CreateLogger("Snapshot"), null!, width, height);

            using var dats = new DefaultDatReaderWriter(datPath, DatAccessType.Read);
            var settings = new WorldBuilderSettings();

            var scratch = Path.Combine(Path.GetTempPath(), "WorldBuilder.Snapshot", "landscape");
            Directory.CreateDirectory(scratch);

            var storage = new FileStorageService(Path.Combine(scratch, "docs"), loggerFactory.CreateLogger<FileStorageService>());
            var documents = new DocumentManager(storage, loggerFactory.CreateLogger<DocumentManager>()) { Dats = dats };
            documents.SetCacheDirectory(Path.Combine(scratch, "cache"));

            var project = new Project {
                Name = "snapshot",
                Guid = Guid.NewGuid(),
                FilePath = Path.Combine(scratch, "snapshot.wbproj"),
                DocumentManager = documents,
                DatReaderWriter = dats,
            };

            // Editor defaults are tuned for an editing session, not for a documentation image: the grid
            // overlay is on, and ambient sits at 0.45 which reads as dusk. Both are settings rather than
            // scene state, so they have to be set before the scene reads them.
            settings.Landscape.Grid.ShowGrid = false;
            settings.Landscape.Rendering.LightIntensity = 0.85f;

            Console.WriteLine("[snapshot] building terrain system (first run scans every landblock in the dats and caches it)...");
            using var terrainSystem = new TerrainSystem(project, dats, settings, loggerFactory.CreateLogger<TerrainSystem>());
            var scene = terrainSystem.Scene;

            // ---- framing, in landblock-frame coordinates like the dungeon path -----------------------
            var lbX = (landblock >> 8) & 0xFF;
            var lbY = landblock & 0xFF;
            var blockOrigin = new Vector3(lbX * 192f, lbY * 192f, 0);

            var (minZ, maxZ) = HeightRange(dats, landblock);
            var centre = blockOrigin + new Vector3(96, 96, (minZ + maxZ) / 2f);

            Vector3 eye, target;
            if (camPos != null && lookAt != null) {
                eye = camPos.Value + blockOrigin;
                target = lookAt.Value + blockOrigin;
            }
            else {
                // Fit the block's real bounding sphere, not just its 192m footprint. Relief is the trap:
                // 0x0405 spans 22-248m, so aiming at the CENTRE VERTEX height put the camera inside the
                // mountain standing next to it. Fit the vertical extent too, and keep the eye clear of the
                // highest ground regardless of what the elevation angle asks for.
                var fov = (float)(settings.Landscape.Camera.FieldOfView * Math.PI / 180.0);
                var radius = Math.Max(136f, (maxZ - minZ) / 2f);
                var distance = radius / MathF.Tan(fov / 2f) * 1.15f;

                var elevation = elevationDegrees * MathF.PI / 180f;
                var horizontal = distance * MathF.Cos(elevation);

                // the camera stands on the named side and looks in across the block
                var offset = heading switch {
                    "N" => new Vector3(0, horizontal, 0),
                    "W" => new Vector3(-horizontal, 0, 0),
                    "E" => new Vector3(horizontal, 0, 0),
                    _ => new Vector3(0, -horizontal, 0),    // S
                };

                var eyeZ = Math.Max(centre.Z + distance * MathF.Sin(elevation), maxZ + 30f);
                eye = new Vector3(centre.X + offset.X, centre.Y + offset.Y, eyeZ);
                target = centre;
            }

            var camera = scene.PerspectiveCamera;
            camera.ScreenSize = new Vector2(width, height);
            camera.SetPosition(eye);
            camera.LookAt(target);

            Console.WriteLine($"[snapshot] eye {eye.X:0},{eye.Y:0},{eye.Z:0} -> target {target.X:0},{target.Y:0},{target.Z:0}   yaw {camera.Yaw:0.#} pitch {camera.Pitch:0.#}");

            // draw distance has to cover the whole framing, or the far half of the block clips away
            settings.Landscape.Camera.MaxDrawDistance = Math.Max(settings.Landscape.Camera.MaxDrawDistance, 6000);

            // ---- offscreen FBO -----------------------------------------------------------------------
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

            // ---- render loop -------------------------------------------------------------------------
            // Terrain chunks, surface textures and scenery stream in over many frames, driven by
            // TerrainSystem.Update from the camera position - the same loop the editor viewport runs.
            //
            // Chunk integration and GPU upload are rate-limited per frame (MaxIntegratePerFrame,
            // MaxGpuUploadsPerFrame), so a fixed frame count is a silent correctness bug: a block that
            // needs more than the budget renders as SKY WITH FLOATING SCENERY - it looks like a camera
            // fault, not a half-loaded scene, and it is easy to accept as "this block is just empty".
            // So run until the loaded-chunk count stops growing, then a little longer for the uploads
            // behind it, rather than trusting a magic number.
            // The convergence signal has to be THE IMAGE, not the loaded-chunk count: the count settles
            // almost immediately (49 chunks within a second) while the geometry behind it is still being
            // uploaded, so waiting on it stops with a frame that is 99% sky. Watch how much of the frame
            // is still empty sky instead, and stop when that stops falling.
            // Chunk generation runs on background threads and the render loop only polls it, so this is
            // WALL-CLOCK bound, not frame bound: spinning frames as fast as possible does not make the
            // terrain arrive sooner. Two traps this has to survive, both of which produced a confident
            // all-sky image during development:
            //   - the loaded-chunk count settles within a second while the geometry is still uploading;
            //   - the sky fraction is perfectly STABLE at ~99.8% before any terrain appears, so plain
            //     "stopped changing" convergence fires before the first triangle is drawn.
            // Hence: a minimum settling time, then convergence on the image, then a hard timeout.
            var aspect = (float)width / height;
            var framesRun = 0;
            var lastSky = 2f;
            var stableBatches = 0;
            byte[] pixels;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            const double minSeconds = 4.0;
            const double maxSeconds = 45.0;

            while (true) {
                for (var i = 0; i < 30 && framesRun < frames; i++, framesRun++) {
                    var viewProjection = camera.GetViewMatrix() * camera.GetProjectionMatrix();
                    terrainSystem.Update(camera.Position, viewProjection);
                    terrainSystem.EditingContext.ClearModifiedLandblocks();

                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                    scene.Render(camera, renderer, aspect, terrainSystem.EditingContext, width, height);
                }

                pixels = CaptureFrame(gl, fbo, width, height);
                var sky = SkyFraction(pixels, width, height);

                stableBatches = Math.Abs(sky - lastSky) < 0.0005f ? stableBatches + 1 : 0;
                lastSky = sky;

                if (clock.Elapsed.TotalSeconds > maxSeconds)
                    break;

                if (clock.Elapsed.TotalSeconds >= minSeconds && stableBatches >= 4)
                    break;

                // give the background chunk builders room rather than burning the GPU on identical frames
                if (framesRun >= frames)
                    System.Threading.Thread.Sleep(50);
            }

            Console.WriteLine($"[snapshot] settled after {clock.Elapsed.TotalSeconds:0.#}s / {framesRun} frames ({scene.GetLoadedChunkCount()} chunks loaded)");

            // A frame that is nearly all sky means the terrain never arrived. Say so: silently writing a
            // blue rectangle is the worst outcome, because it reads as a real empty landblock.
            var skyFraction = SkyFraction(pixels, width, height);
            if (skyFraction > 0.97f) {
                Console.WriteLine($"[snapshot] WARNING: {skyFraction * 100:0.#}% of the frame is empty sky - terrain did not render.");
                Console.WriteLine("[snapshot] Raise --frames, or check the block actually has land (ocean blocks legitimately look like this).");
            }

            // NOTE: no vertical flip here, unlike the dungeon path. PerspectiveCamera - the Landscape
            // editor's own camera, and the one whose conventions the terrain shaders are written against -
            // uses worldUp = -Z with left-handed matrices, so it renders the scene upside-down relative to
            // a conventional camera. GL then reads the framebuffer bottom-up. The two cancel exactly, and
            // reading rows in order gives an upright image; flipping as well puts the sky at the bottom.
            using var image = new Image<Rgba32>(width, height);
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var i = (y * width + x) * 4;
                    image[x, y] = new Rgba32(pixels[i], pixels[i + 1], pixels[i + 2], 255);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath)) ?? ".");
            image.SaveAsPng(pngPath);

            Console.WriteLine($"[snapshot] 0x{landblock:X4} -> {pngPath}");
            Console.WriteLine($"[snapshot] landblock world origin: {blockOrigin.X:0},{blockOrigin.Y:0}   terrain {minZ:0.#}-{maxZ:0.#}m");
            Console.WriteLine($"[snapshot] camera: {(camPos != null ? $"local pos {camPos.Value} look {lookAt!.Value}" : $"landscape, {heading} side, {elevationDegrees:0.#} deg elevation")}");


            window.Close();
            return 0;
        }

        /// <summary>
        /// Share of the frame that is the sky gradient - bluish, with blue clearly dominant and the pixel
        /// bright. Used only to detect the "nothing loaded" failure, so it wants to be conservative:
        /// terrain and water are never this uniformly blue-dominant AND bright.
        /// </summary>
        private static float SkyFraction(byte[] pixels, int width, int height) {
            var sky = 0;
            for (var i = 0; i < width * height; i++) {
                int r = pixels[i * 4], g = pixels[i * 4 + 1], b = pixels[i * 4 + 2];
                if (b > 120 && b > r + 30 && b > g + 15)
                    sky++;
            }
            return (float)sky / (width * height);
        }

        /// <summary>
        /// A landblock's terrain height range, for aiming and fitting the camera. Both ends matter: the
        /// midpoint is what to look at, and the top is what the eye has to clear.
        /// </summary>
        private static (float Min, float Max) HeightRange(IDatReaderWriter dats, uint landblock) {
            if (!dats.TryGet<DatReaderWriter.DBObjs.Region>(0x13000000, out var region))
                return (0, 0);
            if (!dats.TryGet<DatReaderWriter.DBObjs.LandBlock>((landblock << 16) | 0xFFFF, out var lb))
                return (0, 0);

            var table = region.LandDefs.LandHeightTable;
            float min = float.MaxValue, max = float.MinValue;

            for (var i = 0; i < 81; i++) {
                var z = table[lb.Height[i]];
                if (z < min) min = z;
                if (z > max) max = z;
            }

            return (min, max);
        }

        private static byte[] CaptureFrame(GL gl, uint fbo, int width, int height) {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);

            var pixels = new byte[width * height * 4];
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, (Span<byte>)pixels);
            return pixels;
        }

        /// <summary>
        /// Pixel bounding box (raw buffer coordinates) of everything brighter than the scene's
        /// near-black clear color. Null if nothing rendered.
        /// </summary>
        private static (int MinX, int MinY, int MaxX, int MaxY)? MeasureContentBox(byte[] pixels, int width, int height) {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var i = (y * width + x) * 4;
                    if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 48) {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX < 0 ? null : (minX, minY, maxX, maxY);
        }

        private static (int Width, int Height, bool TouchesEdge) MeasureContent(byte[] pixels, int width, int height) {
            var box = MeasureContentBox(pixels, width, height);
            if (box == null)
                return (0, 0, false);
            var (minX, minY, maxX, maxY) = box.Value;
            var touchesEdge = minX <= 1 || minY <= 1 || maxX >= width - 2 || maxY >= height - 2;
            return (maxX - minX + 1, maxY - minY + 1, touchesEdge);
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
