# Playnite Extensions

The repository for all [Playnite](https://github.com/JosefNemec/Playnite) extensions I have created.

## Installation

All extension can be installed directly through the add-on browser in Playnite (`F9`).

## Extensions

### Metadata Image Optimizer

Automatically converts images to a user-defined format, while also resizing all images above a maximum size.  
Users can modify a number of quality-related settings based on the format used to achieve a balance between size and image quality.  

For a full list of features and examples, see this [Wiki page](../../wiki/Metadata-Image-Optimizer).

#### Compression modes

Each image type (background, cover, icon) can be compressed in one of two ways:

| Mode | Behaviour |
| --- | --- |
| **Quality** | Encodes the image once using the quality values configured on the `Quality` tab. |
| **Target size** | Repeatedly re-encodes the image, lowering the quality, until the file fits within the configured size budget (in KB). |

`Target size` is useful when you want a hard guarantee about how much disk space a game's artwork takes up,
for example when keeping a large library on a small drive. For lossy formats (JPG, WebP, JPEG XL) the optimizer
binary searches for the highest quality that still fits. PNG is lossless, so it is first recompressed at the
highest compression level and, if that still isn't enough, quantized to a progressively smaller colour palette.

Two additional options live on the `Quality` tab:

- **Remove metadata** — strips EXIF, IPTC and XMP data (camera information, GPS location, creation dates) from
  optimized images. The colour profile is kept so colours don't shift. The EXIF orientation is applied to the
  pixels before the metadata is removed.
- **Keep the original if the optimized image is larger** — prevents an already optimized image from being
  replaced by a bigger file.

#### JPEG XL

Images can also be converted to JPEG XL (`.jxl`). ImageSharp has no JPEG XL encoder, so the extension shells out
to [libjxl](https://github.com/libjxl/libjxl)'s `cjxl.exe`, which ships in the `Tools` folder (BSD-3-Clause, see
`src/MetadataImageOptimizer/Tools/LICENSE.txt`).

| Setting | cjxl flag | Notes |
| --- | --- | --- |
| JPEG XL Quality | `-q` | 1 to 100, where 100 is mathematically lossless. Defaults to 90. |
| JPEG XL lossless | `-d 0` | Ignores the quality value. Files get larger, and a target size can no longer be met by lowering the quality. |
| JPEG XL Effort | `-e` | 1 to 11. 11 is an expert mode (`--allow_expert_options`, added automatically) that is roughly ten times slower for a few percent smaller lossless files. Defaults to 7. |
| JPEG XL Brotli effort | `--brotli_effort` | 0 to 11, only used for the metadata boxes (colour profile, EXIF, XMP), so it does nothing when metadata is removed. Defaults to 9. |

Encoding time grows with the pixel count and the effort level. The timeout the extension gives `cjxl.exe` is
scaled accordingly (minimum of 5 minutes), so a lossless 1920×620 background at effort 11 can take several
minutes per image — and an image that fails is retried on the next run instead of being cached as done.

Existing `.jxl` images can be re-encoded, including with a target size, but not resized or converted to another
format: libjxl can read JPEG XL, but ImageSharp cannot decode it. Note that Playnite needs a system JPEG XL codec
installed to display these images.

#### Attribution

The `Target size` mode, the metadata stripping and the "keep the original when larger" behaviour are modelled
after [MAZANOKE](https://github.com/civilblur/mazanoke), a self-hosted browser image optimizer.

MAZANOKE is a GPL-3.0 browser application and this extension is MIT licensed, so no MAZANOKE source code was
copied: the compression pipeline in `src/MetadataImageOptimizer/Optimization/MazanokeOptimizer.cs` is an
independent C# implementation built on [ImageSharp](https://github.com/SixLabors/ImageSharp) that reproduces
the same behaviour.
