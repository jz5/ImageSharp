// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using SixLabors.ImageSharp.Common.Helpers;
using SixLabors.ImageSharp.Formats.Jpeg.Components;
using SixLabors.ImageSharp.Formats.Jpeg.Components.Decoder;
using SixLabors.ImageSharp.Formats.Jpeg.Components.Encoder;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.Metadata.Profiles.Iptc;
using SixLabors.ImageSharp.PixelFormats;

namespace SixLabors.ImageSharp.Formats.Jpeg
{
    /// <summary>
    /// Image encoder for writing an image to a stream as a jpeg.
    /// </summary>
    internal sealed unsafe class JpegEncoderCore : IImageEncoderInternals
    {
        /// <summary>
        /// The number of quantization tables.
        /// </summary>
        private const int QuantizationTableCount = 2;

        /// <summary>
        /// A scratch buffer to reduce allocations.
        /// </summary>
        private readonly byte[] buffer = new byte[20];

        /// <summary>
        /// The quality, that will be used to encode the image.
        /// </summary>
        private readonly int? quality;

        /// <summary>
        /// Gets or sets the colorspace to use.
        /// </summary>
        private JpegColorType? colorType;

        /// <summary>
        /// The output stream. All attempted writes after the first error become no-ops.
        /// </summary>
        private Stream outputStream;

        /// <summary>
        /// Initializes a new instance of the <see cref="JpegEncoderCore"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        public JpegEncoderCore(IJpegEncoderOptions options)
        {
            this.quality = options.Quality;

            if (IsSupportedColorType(options.ColorType))
            {
                this.colorType = options.ColorType;
            }
        }

        /// <summary>
        /// Encode writes the image to the jpeg baseline format with the given options.
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="image">The image to write from.</param>
        /// <param name="stream">The stream to write to.</param>
        /// <param name="cancellationToken">The token to request cancellation.</param>
        public void Encode<TPixel>(Image<TPixel> image, Stream stream, CancellationToken cancellationToken)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            Guard.NotNull(image, nameof(image));
            Guard.NotNull(stream, nameof(stream));

            if (image.Width >= JpegConstants.MaxLength || image.Height >= JpegConstants.MaxLength)
            {
                JpegThrowHelper.ThrowDimensionsTooLarge(image.Width, image.Height);
            }

            cancellationToken.ThrowIfCancellationRequested();

            this.outputStream = stream;
            ImageMetadata metadata = image.Metadata;
            JpegMetadata jpegMetadata = metadata.GetJpegMetadata();

            // If the color type was not specified by the user, preserve the color type of the input image.
            if (!this.colorType.HasValue)
            {
                this.colorType = GetFallbackColorType(image);
            }

            // Compute number of components based on color type in options.
            int componentCount = (this.colorType == JpegColorType.Luminance) ? 1 : 3;
            ReadOnlySpan<byte> componentIds = this.GetComponentIds();

            // TODO: Right now encoder writes both quantization tables for grayscale images - we shouldn't do that
            // Initialize the quantization tables.
            this.InitQuantizationTables(componentCount, jpegMetadata, out Block8x8F luminanceQuantTable, out Block8x8F chrominanceQuantTable);

            // Write the Start Of Image marker.
            this.WriteStartOfImage();

            // Do not write APP0 marker for RGB colorspace.
            if (this.colorType != JpegColorType.Rgb)
            {
                this.WriteJfifApplicationHeader(metadata);
            }

            // Write Exif, ICC and IPTC profiles
            this.WriteProfiles(metadata);

            if (this.colorType == JpegColorType.Rgb)
            {
                // Write App14 marker to indicate RGB color space.
                this.WriteApp14Marker();
            }

            // Write the quantization tables.
            this.WriteDefineQuantizationTables(ref luminanceQuantTable, ref chrominanceQuantTable);

            // Write the image dimensions.
            this.WriteStartOfFrame(image.Width, image.Height, componentCount, componentIds);

            // Write the Huffman tables.
            this.WriteDefineHuffmanTables(componentCount);

            // Write the scan header.
            this.WriteStartOfScan(componentCount, componentIds);

            // Write the scan compressed data.
            switch (this.colorType)
            {
                case JpegColorType.YCbCrRatio444:
                    new HuffmanScanEncoder(3, stream).Encode444(image, ref luminanceQuantTable, ref chrominanceQuantTable, cancellationToken);
                    break;
                case JpegColorType.YCbCrRatio420:
                    new HuffmanScanEncoder(6, stream).Encode420(image, ref luminanceQuantTable, ref chrominanceQuantTable, cancellationToken);
                    break;
                case JpegColorType.Luminance:
                    new HuffmanScanEncoder(1, stream).EncodeGrayscale(image, ref luminanceQuantTable, cancellationToken);
                    break;
                case JpegColorType.Rgb:
                    new HuffmanScanEncoder(3, stream).EncodeRgb(image, ref luminanceQuantTable, cancellationToken);
                    break;
                default:
                    // all other non-supported color types are checked at the start of this method
                    break;
            }

            // Write the End Of Image marker.
            this.WriteEndOfImageMarker();

            stream.Flush();
        }

        /// <summary>
        /// If color type was not set, set it based on the given image.
        /// Note, if there is no metadata and the image has multiple components this method
        /// returns <see langword="null"/> defering the field assignment
        /// to <see cref="InitQuantizationTables(int, JpegMetadata, out Block8x8F, out Block8x8F)"/>.
        /// </summary>
        private static JpegColorType? GetFallbackColorType<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            // First inspect the image metadata.
            JpegColorType? colorType = null;
            JpegMetadata metadata = image.Metadata.GetJpegMetadata();
            if (IsSupportedColorType(metadata.ColorType))
            {
                return metadata.ColorType;
            }

            // Secondly, inspect the pixel type.
            // TODO: PixelTypeInfo should contain a component count!
            bool isGrayscale =
                typeof(TPixel) == typeof(L8) || typeof(TPixel) == typeof(L16) ||
                typeof(TPixel) == typeof(La16) || typeof(TPixel) == typeof(La32);

            // We don't set multi-component color types here since we can set it based upon
            // the quality in InitQuantizationTables.
            if (isGrayscale)
            {
                colorType = JpegColorType.Luminance;
            }

            return colorType;
        }

        /// <summary>
        /// Returns true, if the color type is supported by the encoder.
        /// </summary>
        /// <param name="colorType">The color type.</param>
        /// <returns>true, if color type is supported.</returns>
        private static bool IsSupportedColorType(JpegColorType? colorType)
            => colorType == JpegColorType.YCbCrRatio444
            || colorType == JpegColorType.YCbCrRatio420
            || colorType == JpegColorType.Luminance
            || colorType == JpegColorType.Rgb;

        /// <summary>
        /// Gets the component ids.
        /// For color space RGB this will be RGB as ASCII, otherwise 1, 2, 3.
        /// </summary>
        /// <returns>The component Ids.</returns>
        private ReadOnlySpan<byte> GetComponentIds() => this.colorType == JpegColorType.Rgb
                ? new ReadOnlySpan<byte>(new byte[] { 82, 71, 66 })
                : new ReadOnlySpan<byte>(new byte[] { 1, 2, 3 });

        /// <summary>
        /// Writes data to "Define Quantization Tables" block for QuantIndex.
        /// </summary>
        /// <param name="dqt">The "Define Quantization Tables" block.</param>
        /// <param name="offset">Offset in "Define Quantization Tables" block.</param>
        /// <param name="i">The quantization index.</param>
        /// <param name="quant">The quantization table to copy data from.</param>
        private static void WriteDataToDqt(byte[] dqt, ref int offset, QuantIndex i, ref Block8x8F quant)
        {
            dqt[offset++] = (byte)i;
            for (int j = 0; j < Block8x8F.Size; j++)
            {
                dqt[offset++] = (byte)quant[ZigZag.ZigZagOrder[j]];
            }
        }

        /// <summary>
        /// Write the start of image marker.
        /// </summary>
        private void WriteStartOfImage()
        {
            // Markers are always prefixed with 0xff.
            this.buffer[0] = JpegConstants.Markers.XFF;
            this.buffer[1] = JpegConstants.Markers.SOI;

            this.outputStream.Write(this.buffer, 0, 2);
        }

        /// <summary>
        /// Writes the application header containing the JFIF identifier plus extra data.
        /// </summary>
        /// <param name="meta">The image metadata.</param>
        private void WriteJfifApplicationHeader(ImageMetadata meta)
        {
            // Write the JFIF headers
            this.buffer[0] = JpegConstants.Markers.XFF;
            this.buffer[1] = JpegConstants.Markers.APP0; // Application Marker
            this.buffer[2] = 0x00;
            this.buffer[3] = 0x10;
            this.buffer[4] = 0x4a; // J
            this.buffer[5] = 0x46; // F
            this.buffer[6] = 0x49; // I
            this.buffer[7] = 0x46; // F
            this.buffer[8] = 0x00; // = "JFIF",'\0'
            this.buffer[9] = 0x01; // versionhi
            this.buffer[10] = 0x01; // versionlo

            // Resolution. Big Endian
            Span<byte> hResolution = this.buffer.AsSpan(12, 2);
            Span<byte> vResolution = this.buffer.AsSpan(14, 2);

            if (meta.ResolutionUnits == PixelResolutionUnit.PixelsPerMeter)
            {
                // Scale down to PPI
                this.buffer[11] = (byte)PixelResolutionUnit.PixelsPerInch; // xyunits
                BinaryPrimitives.WriteInt16BigEndian(hResolution, (short)Math.Round(UnitConverter.MeterToInch(meta.HorizontalResolution)));
                BinaryPrimitives.WriteInt16BigEndian(vResolution, (short)Math.Round(UnitConverter.MeterToInch(meta.VerticalResolution)));
            }
            else
            {
                // We can simply pass the value.
                this.buffer[11] = (byte)meta.ResolutionUnits; // xyunits
                BinaryPrimitives.WriteInt16BigEndian(hResolution, (short)Math.Round(meta.HorizontalResolution));
                BinaryPrimitives.WriteInt16BigEndian(vResolution, (short)Math.Round(meta.VerticalResolution));
            }

            // No thumbnail
            this.buffer[16] = 0x00; // Thumbnail width
            this.buffer[17] = 0x00; // Thumbnail height

            this.outputStream.Write(this.buffer, 0, 18);
        }

        /// <summary>
        /// Writes the Define Huffman Table marker and tables.
        /// </summary>
        /// <param name="componentCount">The number of components to write.</param>
        private void WriteDefineHuffmanTables(int componentCount)
        {
            // This uses a C#'s compiler optimization that refers to the static data segment of the assembly,
            // and doesn't incur any allocation at all.
            // Table identifiers.
            ReadOnlySpan<byte> headers = new byte[]
            {
                0x00,
                0x10,
                0x01,
                0x11
            };

            int markerlen = 2;
            HuffmanSpec[] specs = HuffmanSpec.TheHuffmanSpecs;

            if (componentCount == 1)
            {
                // Drop the Chrominance tables.
                specs = new[] { HuffmanSpec.TheHuffmanSpecs[0], HuffmanSpec.TheHuffmanSpecs[1] };
            }

            for (int i = 0; i < specs.Length; i++)
            {
                ref HuffmanSpec s = ref specs[i];
                markerlen += 1 + 16 + s.Values.Length;
            }

            this.WriteMarkerHeader(JpegConstants.Markers.DHT, markerlen);
            for (int i = 0; i < specs.Length; i++)
            {
                this.outputStream.WriteByte(headers[i]);
                this.outputStream.Write(specs[i].Count);
                this.outputStream.Write(specs[i].Values);
            }
        }

        /// <summary>
        /// Writes the Define Quantization Marker and tables.
        /// </summary>
        private void WriteDefineQuantizationTables(ref Block8x8F luminanceQuantTable, ref Block8x8F chrominanceQuantTable)
        {
            // Marker + quantization table lengths.
            int markerlen = 2 + (QuantizationTableCount * (1 + Block8x8F.Size));
            this.WriteMarkerHeader(JpegConstants.Markers.DQT, markerlen);

            // Loop through and collect the tables as one array.
            // This allows us to reduce the number of writes to the stream.
            int dqtCount = (QuantizationTableCount * Block8x8F.Size) + QuantizationTableCount;
            byte[] dqt = new byte[dqtCount];
            int offset = 0;

            WriteDataToDqt(dqt, ref offset, QuantIndex.Luminance, ref luminanceQuantTable);
            WriteDataToDqt(dqt, ref offset, QuantIndex.Chrominance, ref chrominanceQuantTable);

            this.outputStream.Write(dqt, 0, dqtCount);
        }

        /// <summary>
        /// Writes the APP14 marker to indicate the image is in RGB color space.
        /// </summary>
        private void WriteApp14Marker()
        {
            this.WriteMarkerHeader(JpegConstants.Markers.APP14, 2 + AdobeMarker.Length);

            // Identifier: ASCII "Adobe".
            this.buffer[0] = 0x41;
            this.buffer[1] = 0x64;
            this.buffer[2] = 0x6F;
            this.buffer[3] = 0x62;
            this.buffer[4] = 0x65;

            // Version, currently 100.
            BinaryPrimitives.WriteInt16BigEndian(this.buffer.AsSpan(5, 2), 100);

            // Flags0
            BinaryPrimitives.WriteInt16BigEndian(this.buffer.AsSpan(7, 2), 0);

            // Flags1
            BinaryPrimitives.WriteInt16BigEndian(this.buffer.AsSpan(9, 2), 0);

            // Transform byte, 0 in combination with three components means the image is in RGB colorspace.
            this.buffer[11] = 0;

            this.outputStream.Write(this.buffer.AsSpan(0, 12));
        }

        /// <summary>
        /// Writes the EXIF profile.
        /// </summary>
        /// <param name="exifProfile">The exif profile.</param>
        private void WriteExifProfile(ExifProfile exifProfile)
        {
            if (exifProfile is null || exifProfile.Values.Count == 0)
            {
                return;
            }

            const int MaxBytesApp1 = 65533; // 64k - 2 padding bytes
            const int MaxBytesWithExifId = 65527; // Max - 6 bytes for EXIF header.

            byte[] data = exifProfile.ToByteArray();

            if (data.Length == 0)
            {
                return;
            }

            // We can write up to a maximum of 64 data to the initial marker so calculate boundaries.
            int exifMarkerLength = ProfileResolver.ExifMarker.Length;
            int remaining = exifMarkerLength + data.Length;
            int bytesToWrite = remaining > MaxBytesApp1 ? MaxBytesApp1 : remaining;
            int app1Length = bytesToWrite + 2;

            // Write the app marker, EXIF marker, and data
            this.WriteApp1Header(app1Length);
            this.outputStream.Write(ProfileResolver.ExifMarker);
            this.outputStream.Write(data, 0, bytesToWrite - exifMarkerLength);
            remaining -= bytesToWrite;

            // If the exif data exceeds 64K, write it in multiple APP1 Markers
            for (int idx = MaxBytesWithExifId; idx < data.Length; idx += MaxBytesWithExifId)
            {
                bytesToWrite = remaining > MaxBytesWithExifId ? MaxBytesWithExifId : remaining;
                app1Length = bytesToWrite + 2 + exifMarkerLength;

                this.WriteApp1Header(app1Length);

                // Write Exif00 marker
                this.outputStream.Write(ProfileResolver.ExifMarker);

                // Write the exif data
                this.outputStream.Write(data, idx, bytesToWrite);

                remaining -= bytesToWrite;
            }
        }

        /// <summary>
        /// Writes the IPTC metadata.
        /// </summary>
        /// <param name="iptcProfile">The iptc metadata to write.</param>
        /// <exception cref="ImageFormatException">
        /// Thrown if the IPTC profile size exceeds the limit of 65533 bytes.
        /// </exception>
        private void WriteIptcProfile(IptcProfile iptcProfile)
        {
            const int Max = 65533;
            if (iptcProfile is null || !iptcProfile.Values.Any())
            {
                return;
            }

            iptcProfile.UpdateData();
            byte[] data = iptcProfile.Data;
            if (data.Length == 0)
            {
                return;
            }

            if (data.Length > Max)
            {
                throw new ImageFormatException($"Iptc profile size exceeds limit of {Max} bytes");
            }

            int app13Length = 2 + ProfileResolver.AdobePhotoshopApp13Marker.Length +
                              ProfileResolver.AdobeImageResourceBlockMarker.Length +
                              ProfileResolver.AdobeIptcMarker.Length +
                              2 + 4 + data.Length;
            this.WriteAppHeader(app13Length, JpegConstants.Markers.APP13);
            this.outputStream.Write(ProfileResolver.AdobePhotoshopApp13Marker);
            this.outputStream.Write(ProfileResolver.AdobeImageResourceBlockMarker);
            this.outputStream.Write(ProfileResolver.AdobeIptcMarker);
            this.outputStream.WriteByte(0); // a empty pascal string (padded to make size even)
            this.outputStream.WriteByte(0);
            BinaryPrimitives.WriteInt32BigEndian(this.buffer, data.Length);
            this.outputStream.Write(this.buffer, 0, 4);
            this.outputStream.Write(data, 0, data.Length);
        }

        /// <summary>
        /// Writes the App1 header.
        /// </summary>
        /// <param name="app1Length">The length of the data the app1 marker contains.</param>
        private void WriteApp1Header(int app1Length)
            => this.WriteAppHeader(app1Length, JpegConstants.Markers.APP1);

        /// <summary>
        /// Writes a AppX header.
        /// </summary>
        /// <param name="length">The length of the data the app marker contains.</param>
        /// <param name="appMarker">The app marker to write.</param>
        private void WriteAppHeader(int length, byte appMarker)
        {
            this.buffer[0] = JpegConstants.Markers.XFF;
            this.buffer[1] = appMarker;
            this.buffer[2] = (byte)((length >> 8) & 0xFF);
            this.buffer[3] = (byte)(length & 0xFF);

            this.outputStream.Write(this.buffer, 0, 4);
        }

        /// <summary>
        /// Writes the ICC profile.
        /// </summary>
        /// <param name="iccProfile">The ICC profile to write.</param>
        /// <exception cref="ImageFormatException">
        /// Thrown if any of the ICC profiles size exceeds the limit.
        /// </exception>
        private void WriteIccProfile(IccProfile iccProfile)
        {
            if (iccProfile is null)
            {
                return;
            }

            const int IccOverheadLength = 14;
            const int Max = 65533;
            const int MaxData = Max - IccOverheadLength;

            byte[] data = iccProfile.ToByteArray();

            if (data is null || data.Length == 0)
            {
                return;
            }

            // Calculate the number of markers we'll need, rounding up of course.
            int dataLength = data.Length;
            int count = dataLength / MaxData;

            if (count * MaxData != dataLength)
            {
                count++;
            }

            // Per spec, counting starts at 1.
            int current = 1;
            int offset = 0;

            while (dataLength > 0)
            {
                int length = dataLength; // Number of bytes to write.

                if (length > MaxData)
                {
                    length = MaxData;
                }

                dataLength -= length;

                this.buffer[0] = JpegConstants.Markers.XFF;
                this.buffer[1] = JpegConstants.Markers.APP2; // Application Marker
                int markerLength = length + 16;
                this.buffer[2] = (byte)((markerLength >> 8) & 0xFF);
                this.buffer[3] = (byte)(markerLength & 0xFF);

                this.outputStream.Write(this.buffer, 0, 4);

                this.buffer[0] = (byte)'I';
                this.buffer[1] = (byte)'C';
                this.buffer[2] = (byte)'C';
                this.buffer[3] = (byte)'_';
                this.buffer[4] = (byte)'P';
                this.buffer[5] = (byte)'R';
                this.buffer[6] = (byte)'O';
                this.buffer[7] = (byte)'F';
                this.buffer[8] = (byte)'I';
                this.buffer[9] = (byte)'L';
                this.buffer[10] = (byte)'E';
                this.buffer[11] = 0x00;
                this.buffer[12] = (byte)current; // The position within the collection.
                this.buffer[13] = (byte)count; // The total number of profiles.

                this.outputStream.Write(this.buffer, 0, IccOverheadLength);
                this.outputStream.Write(data, offset, length);

                current++;
                offset += length;
            }
        }

        /// <summary>
        /// Writes the metadata profiles to the image.
        /// </summary>
        /// <param name="metadata">The image metadata.</param>
        private void WriteProfiles(ImageMetadata metadata)
        {
            if (metadata is null)
            {
                return;
            }

            metadata.SyncProfiles();
            this.WriteExifProfile(metadata.ExifProfile);
            this.WriteIccProfile(metadata.IccProfile);
            this.WriteIptcProfile(metadata.IptcProfile);
        }

        /// <summary>
        /// Writes the Start Of Frame (Baseline) marker.
        /// </summary>
        /// <param name="width">The width of the image.</param>
        /// <param name="height">The height of the image.</param>
        /// <param name="componentCount">The number of components in a pixel.</param>
        /// <param name="componentIds">The component Id's.</param>
        private void WriteStartOfFrame(int width, int height, int componentCount, ReadOnlySpan<byte> componentIds)
        {
            // This uses a C#'s compiler optimization that refers to the static data segment of the assembly,
            // and doesn't incur any allocation at all.
            // "default" to 4:2:0
            ReadOnlySpan<byte> subsamples = new byte[]
            {
                0x22,
                0x11,
                0x11
            };

            ReadOnlySpan<byte> chroma = new byte[]
            {
                0x00,
                0x01,
                0x01
            };

            if (this.colorType == JpegColorType.Luminance)
            {
                subsamples = new byte[]
                {
                    0x11,
                    0x00,
                    0x00
                };
            }
            else
            {
                switch (this.colorType)
                {
                    case JpegColorType.YCbCrRatio444:
                    case JpegColorType.Rgb:
                        subsamples = new byte[]
                        {
                            0x11,
                            0x11,
                            0x11
                        };

                        if (this.colorType == JpegColorType.Rgb)
                        {
                            chroma = new byte[]
                            {
                                0x00,
                                0x00,
                                0x00
                            };
                        }

                        break;
                    case JpegColorType.YCbCrRatio420:
                        subsamples = new byte[]
                        {
                            0x22,
                            0x11,
                            0x11
                        };
                        break;
                }
            }

            // Length (high byte, low byte), 8 + components * 3.
            int markerlen = 8 + (3 * componentCount);
            this.WriteMarkerHeader(JpegConstants.Markers.SOF0, markerlen);
            this.buffer[0] = 8; // Data Precision. 8 for now, 12 and 16 bit jpegs not supported
            this.buffer[1] = (byte)(height >> 8);
            this.buffer[2] = (byte)(height & 0xff); // (2 bytes, Hi-Lo), must be > 0 if DNL not supported
            this.buffer[3] = (byte)(width >> 8);
            this.buffer[4] = (byte)(width & 0xff); // (2 bytes, Hi-Lo), must be > 0 if DNL not supported
            this.buffer[5] = (byte)componentCount;

            for (int i = 0; i < componentCount; i++)
            {
                int i3 = 3 * i;

                // Component ID.
                Span<byte> bufferSpan = this.buffer.AsSpan(i3 + 6, 3);
                bufferSpan[2] = chroma[i];
                bufferSpan[1] = subsamples[i];
                bufferSpan[0] = componentIds[i];
            }

            this.outputStream.Write(this.buffer, 0, (3 * (componentCount - 1)) + 9);
        }

        /// <summary>
        /// Writes the StartOfScan marker.
        /// </summary>
        /// <param name="componentCount">The number of components in a pixel.</param>
        /// <param name="componentIds">The componentId's.</param>
        private void WriteStartOfScan(int componentCount, ReadOnlySpan<byte> componentIds)
        {
            // This uses a C#'s compiler optimization that refers to the static data segment of the assembly,
            // and doesn't incur any allocation at all.
            ReadOnlySpan<byte> huffmanId = new byte[]
            {
                0x00,
                0x11,
                0x11
            };

            // Use the same DC/AC tables for all channels for RGB.
            if (this.colorType == JpegColorType.Rgb)
            {
                huffmanId = new byte[]
                {
                    0x00,
                    0x00,
                    0x00
                };
            }

            // Write the SOS (Start Of Scan) marker "\xff\xda" followed by 12 bytes:
            // - the marker length "\x00\x0c",
            // - the number of components "\x03",
            // - component 1 uses DC table 0 and AC table 0 "\x01\x00",
            // - component 2 uses DC table 1 and AC table 1 "\x02\x11",
            // - component 3 uses DC table 1 and AC table 1 "\x03\x11",
            // - the bytes "\x00\x3f\x00". Section B.2.3 of the spec says that for
            // sequential DCTs, those bytes (8-bit Ss, 8-bit Se, 4-bit Ah, 4-bit Al)
            // should be 0x00, 0x3f, 0x00&lt;&lt;4 | 0x00.
            this.buffer[0] = JpegConstants.Markers.XFF;
            this.buffer[1] = JpegConstants.Markers.SOS;

            // Length (high byte, low byte), must be 6 + 2 * (number of components in scan)
            int sosSize = 6 + (2 * componentCount);
            this.buffer[2] = 0x00;
            this.buffer[3] = (byte)sosSize;
            this.buffer[4] = (byte)componentCount; // Number of components in a scan
            for (int i = 0; i < componentCount; i++)
            {
                int i2 = 2 * i;
                this.buffer[i2 + 5] = componentIds[i]; // Component Id
                this.buffer[i2 + 6] = huffmanId[i]; // DC/AC Huffman table
            }

            this.buffer[sosSize - 1] = 0x00; // Ss - Start of spectral selection.
            this.buffer[sosSize] = 0x3f; // Se - End of spectral selection.
            this.buffer[sosSize + 1] = 0x00; // Ah + Ah (Successive approximation bit position high + low)
            this.outputStream.Write(this.buffer, 0, sosSize + 2);
        }

        /// <summary>
        /// Writes the EndOfImage marker.
        /// </summary>
        private void WriteEndOfImageMarker()
        {
            this.buffer[0] = JpegConstants.Markers.XFF;
            this.buffer[1] = JpegConstants.Markers.EOI;
            this.outputStream.Write(this.buffer, 0, 2);
        }

        /// <summary>
        /// Writes the header for a marker with the given length.
        /// </summary>
        /// <param name="marker">The marker to write.</param>
        /// <param name="length">The marker length.</param>
        private void WriteMarkerHeader(byte marker, int length)
        {
            // Markers are always prefixed with 0xff.
            this.buffer[0] = JpegConstants.Markers.XFF;
            this.buffer[1] = marker;
            this.buffer[2] = (byte)(length >> 8);
            this.buffer[3] = (byte)(length & 0xff);
            this.outputStream.Write(this.buffer, 0, 4);
        }

        /// <summary>
        /// Initializes quantization tables.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Zig-zag ordering is NOT applied to the resulting tables.
        /// </para>
        /// <para>
        /// We take quality values in a hierarchical order:
        /// 1. Check if encoder has set quality
        /// 2. Check if metadata has set quality
        /// 3. Take default quality value - 75
        /// </para>
        /// </remarks>
        /// <param name="componentCount">Color components count.</param>
        /// <param name="metadata">Jpeg metadata instance.</param>
        /// <param name="luminanceQuantTable">Output luminance quantization table.</param>
        /// <param name="chrominanceQuantTable">Output chrominance quantization table.</param>
        private void InitQuantizationTables(int componentCount, JpegMetadata metadata, out Block8x8F luminanceQuantTable, out Block8x8F chrominanceQuantTable)
        {
            int lumaQuality;
            int chromaQuality;
            if (this.quality.HasValue)
            {
                lumaQuality = this.quality.Value;
                chromaQuality = this.quality.Value;
            }
            else
            {
                lumaQuality = metadata.LuminanceQuality;
                chromaQuality = metadata.ChrominanceQuality;
            }

            // Luminance
            lumaQuality = Numerics.Clamp(lumaQuality, 1, 100);
            luminanceQuantTable = Quantization.ScaleLuminanceTable(lumaQuality);

            // Chrominance
            chrominanceQuantTable = default;
            if (componentCount > 1)
            {
                chromaQuality = Numerics.Clamp(chromaQuality, 1, 100);
                chrominanceQuantTable = Quantization.ScaleChrominanceTable(chromaQuality);

                if (!this.colorType.HasValue)
                {
                    this.colorType = chromaQuality >= 91 ? JpegColorType.YCbCrRatio444 : JpegColorType.YCbCrRatio420;
                }
            }
        }
    }
}
