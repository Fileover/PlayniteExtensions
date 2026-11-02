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
for example when keeping a large library on a small drive. For lossy formats (JPG, WebP) the optimizer binary
searches for the highest quality that still fits. PNG is lossless, so it is first recompressed at the highest
compression level and, if that still isn't enough, quantized to a progressively smaller colour palette.

Two additional options live on the `Quality` tab:

- **Remove metadata** — strips EXIF, IPTC and XMP data (camera information, GPS location, creation dates) from
  optimized images. The colour profile is kept so colours don't shift. The EXIF orientation is applied to the
  pixels before the metadata is removed.
- **Keep the original if the optimized image is larger** — prevents an already optimized image from being
  replaced by a bigger file.

#### Attribution

The `Target size` mode, the metadata stripping and the "keep the original when larger" behaviour are modelled
after [MAZANOKE](https://github.com/civilblur/mazanoke), a self-hosted browser image optimizer.

MAZANOKE is a GPL-3.0 browser application and this extension is MIT licensed, so no MAZANOKE source code was
copied: the compression pipeline in `src/MetadataImageOptimizer/Optimization/MazanokeOptimizer.cs` is an
independent C# implementation built on [ImageSharp](https://github.com/SixLabors/ImageSharp) that reproduces
the same behaviour.
