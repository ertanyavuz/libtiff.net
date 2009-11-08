﻿/* Copyright (C) 2008-2009, Bit Miracle
 * http://www.bitmiracle.com
 * 
 * This software is based in part on the work of the Sam Leffler, Silicon 
 * Graphics, Inc. and contributors.
 *
 * Copyright (c) 1988-1997 Sam Leffler
 * Copyright (c) 1991-1997 Silicon Graphics, Inc.
 * For conditions of distribution and use, see the accompanying README file.
 */

/*
 * CCITT Group 3 (T.4) and Group 4 (T.6) Compression Support.
 *
 * This file contains support for decoding and encoding TIFF
 * compression algorithms 2, 3, 4, and 32771.
 *
 * Decoder support is derived, with permission, from the code
 * in Frank Cringle's viewfax program;
 *      Copyright (C) 1990, 1995  Frank D. Cringle.
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

namespace BitMiracle.LibTiff.Internal
{
    partial class CCITTCodec : TiffCodec
    {
        /*
        * To override the default routine used to image decoded
        * spans one can use the pseduo tag TIFFTAG_FAXFILLFUNC.
        * The routine must have the type signature given below;
        * for example:
        *
        * fillruns(byte[] buf, int startOffset, int[] runs, int thisrun, int erun, int lastx)
        *
        * where buf is place to set the bits, startOffset is index of first byte to process,
        * runs is the array of b&w run lengths (white then black), thisrun is current 
        * row's run array index, erun is the index of last run in the array, and
        * lastx is the width of the row in pixels.  Fill routines can assume
        * the run array has room for at least lastx runs and can overwrite
        * data in the run array as needed (e.g. to append zero runs to bring
        * the count up to a nice multiple).
        */
        public delegate void FaxFillFunc(byte[] buf, int startOffset, int[] runs, int thisrun, int erun, int lastx);

        public const int FIELD_BADFAXLINES = (FIELD.FIELD_CODEC + 0);
        public const int FIELD_CLEANFAXDATA = (FIELD.FIELD_CODEC + 1);
        public const int FIELD_BADFAXRUN = (FIELD.FIELD_CODEC + 2);
        public const int FIELD_RECVPARAMS = (FIELD.FIELD_CODEC + 3);
        public const int FIELD_SUBADDRESS = (FIELD.FIELD_CODEC + 4);
        public const int FIELD_RECVTIME = (FIELD.FIELD_CODEC + 5);
        public const int FIELD_FAXDCS = (FIELD.FIELD_CODEC + 6);
        public const int FIELD_OPTIONS = (FIELD.FIELD_CODEC + 7);

        internal FAXMODE m_mode; /* operating mode */
        internal GROUP3OPT m_groupoptions; /* Group 3/4 options tag */
        internal CLEANFAXDATA m_cleanfaxdata; /* CleanFaxData tag */
        internal uint m_badfaxlines; /* BadFaxLines tag */
        internal uint m_badfaxrun; /* BadFaxRun tag */
        internal uint m_recvparams; /* encoded Class 2 session params */
        internal string m_subaddress; /* subaddress string */
        internal uint m_recvtime; /* time spent receiving (secs) */
        internal string m_faxdcs; /* Table 2/T.30 encoded session params */

        /* Decoder state info */
        internal FaxFillFunc fill; /* fill routine */

        private const int EOL_CODE = 0x001;   /* EOL code value - 0000 0000 0000 1 */

        /* finite state machine codes */
        private const byte S_Null = 0;
        private const byte S_Pass = 1;
        private const byte S_Horiz = 2;
        private const byte S_V0 = 3;
        private const byte S_VR = 4;
        private const byte S_VL = 5;
        private const byte S_Ext = 6;
        private const byte S_TermW = 7;
        private const byte S_TermB = 8;
        private const byte S_MakeUpW = 9;
        private const byte S_MakeUpB = 10;
        private const byte S_MakeUp = 11;
        private const byte S_EOL = 12;

        /* status values returned instead of a run length */
        private const short G3CODE_EOL = -1;  /* NB: ACT_EOL - ACT_WRUNT */
        private const short G3CODE_INVALID = -2;  /* NB: ACT_INVALID - ACT_WRUNT */
        private const short G3CODE_EOF = -3;  /* end of input data */
        private const short G3CODE_INCOMP = -4;  /* incomplete run code */

        /*
        * CCITT T.4 1D Huffman runlength codes and
        * related definitions.  Given the small sizes
        * of these tables it does not seem
        * worthwhile to make code & length 8 bits.
        */
        private struct tableEntry
        {
            public tableEntry(ushort _length, ushort _code, short _runlen)
            {
                length = _length;
                code = _code;
                runlen = _runlen;
            }

            public static tableEntry FromArray(short[] array, int entryNumber)
            {
                int offset = entryNumber * 3; // we have 3 elements in entry
                return new tableEntry((ushort)array[offset], (ushort)array[offset + 1], array[offset + 2]);
            }

            public ushort length; /* bit length of g3 code */
            public ushort code; /* g3 code */
            public short runlen; /* run length in bits */
        };

        private struct faxTableEntry
        {
            public faxTableEntry(byte _State, byte _Width, int _Param)
            {
                State = _State;
                Width = _Width;
                Param = _Param;
            }

            public static faxTableEntry FromArray(int[] array, int entryNumber)
            {
                int offset = entryNumber * 3; // we have 3 elements in entry
                return new faxTableEntry((byte)array[offset], (byte)array[offset + 1], array[offset + 2]);
            }

            /* state table entry */
            public byte State; /* see above */
            public byte Width; /* width of code in bits */
            public int Param; /* unsigned 32-bit run length in bits */
        };

        private enum Decoder
        {
            useFax3_1DDecoder,
            useFax3_2DDecoder,
            useFax4Decoder,
            useFax3RLEDecoder
        };
        
        private enum Fax3Encoder
        {
            useFax1DEncoder, 
            useFax2DEncoder
        };

        private static TiffFieldInfo[] m_faxFieldInfo =
        {
            new TiffFieldInfo(TIFFTAG.TIFFTAG_FAXMODE, 0, 0, TiffDataType.TIFF_ANY, FIELD.FIELD_PSEUDO, false, false, "FaxMode"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_FAXFILLFUNC, 0, 0, TiffDataType.TIFF_ANY, FIELD.FIELD_PSEUDO, false, false, "FaxFillFunc"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_BADFAXLINES, 1, 1, TiffDataType.TIFF_LONG, FIELD_BADFAXLINES, true, false, "BadFaxLines"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_BADFAXLINES, 1, 1, TiffDataType.TIFF_SHORT, FIELD_BADFAXLINES, true, false, "BadFaxLines"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_CLEANFAXDATA, 1, 1, TiffDataType.TIFF_SHORT, FIELD_CLEANFAXDATA, true, false, "CleanFaxData"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_CONSECUTIVEBADFAXLINES, 1, 1, TiffDataType.TIFF_LONG, FIELD_BADFAXRUN, true, false, "ConsecutiveBadFaxLines"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_CONSECUTIVEBADFAXLINES, 1, 1, TiffDataType.TIFF_SHORT, FIELD_BADFAXRUN, true, false, "ConsecutiveBadFaxLines"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_FAXRECVPARAMS, 1, 1, TiffDataType.TIFF_LONG, FIELD_RECVPARAMS, true, false, "FaxRecvParams"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_FAXSUBADDRESS, -1, -1, TiffDataType.TIFF_ASCII, FIELD_SUBADDRESS, true, false, "FaxSubAddress"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_FAXRECVTIME, 1, 1, TiffDataType.TIFF_LONG, FIELD_RECVTIME, true, false, "FaxRecvTime"), 
            new TiffFieldInfo(TIFFTAG.TIFFTAG_FAXDCS, -1, -1, TiffDataType.TIFF_ASCII, FIELD_FAXDCS, true, false, "FaxDcs"), 
        };
        
        private static TiffFieldInfo[] m_fax3FieldInfo = 
        {
            new TiffFieldInfo(TIFFTAG.TIFFTAG_GROUP3OPTIONS, 1, 1, TiffDataType.TIFF_LONG, FIELD_OPTIONS, false, false, "Group3Options"), 
        };

        private static TiffFieldInfo[] m_fax4FieldInfo = 
        {
            new TiffFieldInfo(TIFFTAG.TIFFTAG_GROUP4OPTIONS, 1, 1, TiffDataType.TIFF_LONG, FIELD_OPTIONS, false, false, "Group4Options"), 
        };

        private TiffTagMethods m_parentTagMethods;
        private TiffTagMethods m_tagMethods;

        private int m_rw_mode; /* O_RDONLY for decode, else encode */
        private int m_rowbytes; /* bytes in a decoded scanline */
        private int m_rowpixels; /* pixels in a scanline */

        /* Decoder state info */
        private Decoder m_decoder;
        private byte[] m_bitmap; /* bit reversal table */
        private int m_data; /* current i/o byte/word */
        private int m_bit; /* current i/o bit in byte */
        private int m_EOLcnt; /* count of EOL codes recognized */
        private int[] m_runs; /* b&w runs for current/previous row */
        private int m_refruns; /* runs for reference line (index in m_runs) */
        private int m_curruns; /* runs for current line (index in m_runs) */

        private int m_a0; /* reference element */
        private int m_RunLength; /* length of current run */
        private int m_thisrun; /* current row's run array (index in m_runs) */
        private int m_pa; /* place to stuff next run (index in m_runs) */
        private int m_pb; /* next run in reference line (index in m_runs) */

        /* Encoder state info */
        private Fax3Encoder m_encoder; /* encoding state */
        private bool m_encodingFax4; // if false, G3 will be used
        private byte[] m_refline; /* reference line for 2d decoding */
        private int m_k; /* #rows left that can be 2d encoded */
        private int m_maxk; /* max #rows that can be 2d encoded */
        private int m_line;

        private byte[] m_bp; // pointer to data to encode
        private int m_bpPos;   // current position in m_bp

        public CCITTCodec(Tiff tif, COMPRESSION scheme, string name)
            : base(tif, scheme, name)
        {
            m_tagMethods = new CCITTCodecTagMethods();
        }

        public override bool Init()
        {
            switch (m_scheme)
            {
                case COMPRESSION.COMPRESSION_CCITTRLE:
                    return TIFFInitCCITTRLE();
                case COMPRESSION.COMPRESSION_CCITTRLEW:
                    return TIFFInitCCITTRLEW();
                case COMPRESSION.COMPRESSION_CCITTFAX3:
                    return TIFFInitCCITTFax3();
                case COMPRESSION.COMPRESSION_CCITTFAX4:
                    return TIFFInitCCITTFax4();
            }

            return false;
        }

        public override bool CanEncode()
        {
            return true;
        }

        public override bool CanDecode()
        {
            return true;
        }

        public override bool tif_setupdecode()
        {
            // same for all types
            return setupState();
        }

        public override bool tif_predecode(ushort s)
        {
            m_bit = 0; /* force initial read */
            m_data = 0;
            m_EOLcnt = 0; /* force initial scan for EOL */

            /*
            * Decoder assumes lsb-to-msb bit order.  Note that we select
            * this here rather than in setupState so that viewers can
            * hold the image open, fiddle with the FillOrder tag value,
            * and then re-decode the image.  Otherwise they'd need to close
            * and open the image to get the state reset.
            */
            m_bitmap = Tiff.GetBitRevTable(m_tif.m_dir.td_fillorder != FILLORDER.FILLORDER_LSB2MSB);
            if (m_refruns >= 0)
            {
                /* init reference line to white */
                m_runs[m_refruns] = m_rowpixels;
                m_runs[m_refruns + 1] = 0;
            }
            
            m_line = 0;
            return true;
        }

        public override bool tif_decoderow(byte[] pp, int cc, ushort s)
        {
            switch (m_decoder)
            {
                case Decoder.useFax3_1DDecoder:
                    return Fax3Decode1D(pp, cc);
                case Decoder.useFax3_2DDecoder:
                    return Fax3Decode2D(pp, cc);
                case Decoder.useFax4Decoder:
                    return Fax4Decode(pp, cc);
                case Decoder.useFax3RLEDecoder:
                    return Fax3DecodeRLE(pp, cc);
            }

            return false;
        }

        public override bool tif_decodestrip(byte[] pp, int cc, ushort s)
        {
            return tif_decoderow(pp, cc, s);
        }

        public override bool tif_decodetile(byte[] pp, int cc, ushort s)
        {
            return tif_decoderow(pp, cc, s);
        }

        public override bool tif_setupencode()
        {
            // same for all types
            return setupState();
        }

        public override bool tif_preencode(ushort s)
        {
            m_bit = 8;
            m_data = 0;
            m_encoder = Fax3Encoder.useFax1DEncoder;

            /*
            * This is necessary for Group 4; otherwise it isn't
            * needed because the first scanline of each strip ends
            * up being copied into the refline.
            */
            if (m_refline != null)
                Array.Clear(m_refline, 0, m_refline.Length);

            if (is2DEncoding())
            {
                float res = m_tif.m_dir.td_yresolution;
                /*
                * The CCITT spec says that when doing 2d encoding, you
                * should only do it on K consecutive scanlines, where K
                * depends on the resolution of the image being encoded
                * (2 for <= 200 lpi, 4 for > 200 lpi).  Since the directory
                * code initializes td_yresolution to 0, this code will
                * select a K of 2 unless the YResolution tag is set
                * appropriately.  (Note also that we fudge a little here
                * and use 150 lpi to avoid problems with units conversion.)
                */
                if (m_tif.m_dir.td_resolutionunit == RESUNIT.RESUNIT_CENTIMETER)
                {
                    /* convert to inches */
                    res *= 2.54f;
                }

                m_maxk = (res > 150 ? 4 : 2);
                m_k = m_maxk - 1;
            }
            else
            {
                m_maxk = 0;
                m_k = 0;
            }

            m_line = 0;
            return true;
        }

        public override bool tif_postencode()
        {
            if (m_encodingFax4)
                return Fax4PostEncode();

            return Fax3PostEncode();
        }

        public override bool tif_encoderow(byte[] pp, int cc, ushort s)
        {
            if (m_encodingFax4)
                return Fax4Encode(pp, cc);

            return Fax3Encode(pp, cc);
        }

        public override bool tif_encodestrip(byte[] pp, int cc, ushort s)
        {
            return tif_encoderow(pp, cc, s);
        }

        public override bool tif_encodetile(byte[] pp, int cc, ushort s)
        {
            return tif_encoderow(pp, cc, s);
        }

        public override void tif_close()
        {
            if ((m_mode & FAXMODE.FAXMODE_NORTC) == 0)
            {
                int code = EOL_CODE;
                int length = 12;
                if (is2DEncoding())
                {
                    bool b = ((code << 1) != 0) | (m_encoder == Fax3Encoder.useFax1DEncoder);
                    if (b)
                        code = 1;
                    else
                        code = 0;

                    length++;
                }

                for (int i = 0; i < 6; i++)
                    putBits(code, length);

                flushBits();
            }
        }

        public override void tif_cleanup()
        {
            m_tif.m_tagmethods = m_tagMethods;
        }

        private bool is2DEncoding()
        {
            return (m_groupoptions & GROUP3OPT.GROUP3OPT_2DENCODING) != 0;
        }

        /*
        * Update the value of b1 using the array
        * of runs for the reference line.
        */
        private void CHECK_b1(ref int b1)
        {
            if (m_pa != m_thisrun)
            {
                while (b1 <= m_a0 && b1 < (int)m_rowpixels)
                {
                    b1 += m_runs[m_pb] + m_runs[m_pb + 1];
                    m_pb += 2;
                }
            }
        }

        private static void SWAP(ref int a, ref int b)
        {
            int x = a;
            a = b;
            b = x;
        }

        private static bool isLongAligned(int offset)
        {
            return (offset % sizeof(int) == 0);
        }

        private static bool isUint16Aligned(int offset)
        {
            return (offset % sizeof(ushort) == 0);
        }

        /*
        * The FILL macro must handle spans < 2*sizeof(int) bytes.
        * This is <8 bytes.
        */
        private static void FILL(int n, byte[] cp, ref int offset, byte value)
        {
            const int max = 7;

            if (n <= max && n > 0)
            {
                for (int i = n; i > 0; i--)
                    cp[offset + i - 1] = value;

                offset += n;
            }
        }

        /*
        * Bit-fill a row according to the white/black
        * runs generated during G3/G4 decoding.
        * The default run filler; made public for other decoders.
        */
        private static void fax3FillRuns(byte[] buf, int startOffset, int[] runs, int thisrun, int erun, int lastx)
        {
            if (((erun - thisrun) & 1) != 0)
            {
                runs[erun] = 0;
                erun++;
            }

            int x = 0;
            for (; thisrun < erun; thisrun += 2)
            {
                int run = runs[thisrun];
                if (x + run > lastx || run > lastx)
                {
                    runs[thisrun] = lastx - x;
                    run = runs[thisrun];
                }

                if (run != 0)
                {
                    int cp = startOffset + (x >> 3);
                    int bx = x & 7;
                    if (run > 8 - bx)
                    {
                        if (bx != 0)
                        {
                            /* align to byte boundary */
                            buf[cp] &= (byte)(0xff << (8 - bx));
                            cp++;
                            run -= 8 - bx;
                        }

                        int n = run >> 3;
                        if (n != 0)
                        {
                            /* multiple bytes to fill */
                            if ((n / sizeof(int)) > 1)
                            {
                                /*
                                 * Align to longword boundary and fill.
                                 */
                                for ( ; n != 0 && !isLongAligned(cp); n--)
                                {
                                    buf[cp] = 0x00;
                                    cp++;
                                }

                                int bytesToFill = n - (n % sizeof(int));
                                n -= bytesToFill;
                                
                                int stop = bytesToFill + cp;
                                for ( ; cp < stop; cp++)
                                    buf[cp] = 0;
                            }

                            FILL(n, buf, ref cp, 0);
                            run &= 7;
                        }

                        if (run != 0)
                            buf[cp] &= (byte)(0xff >> run);
                    }
                    else
                        buf[cp] &= (byte)(~(fillMasks[run] >> bx));

                    x += runs[thisrun];
                }

                run = runs[thisrun + 1];
                if (x + run > lastx || run > lastx)
                {
                    runs[thisrun + 1] = lastx - x;
                    run = runs[thisrun + 1];
                }
                
                if (run != 0)
                {
                    int cp = startOffset + (x >> 3);
                    int bx = x & 7;
                    if (run > 8 - bx)
                    {
                        if (bx != 0)
                        {
                            /* align to byte boundary */
                            buf[cp] |= (byte)(0xff >> bx);
                            cp++;
                            run -= 8 - bx;
                        }

                        int n = run >> 3;
                        if (n != 0)
                        {
                            /* multiple bytes to fill */
                            if ((n / sizeof(int)) > 1)
                            {
                                /*
                                 * Align to longword boundary and fill.
                                 */
                                for ( ; n != 0 && !isLongAligned(cp); n--)
                                {
                                    buf[cp] = 0xff;
                                    cp++;
                                }
                                
                                int bytesToFill = n - (n % sizeof(int));
                                n -= bytesToFill;
                                
                                int stop = bytesToFill + cp;
                                for ( ; cp < stop; cp++)
                                    buf[cp] = 0xff;
                            }

                            FILL(n, buf, ref cp, 0xff);
                            run &= 7;
                        }

                        if (run != 0)
                            buf[cp] |= (byte)(0xff00 >> run);
                    }
                    else
                        buf[cp] |= (byte)(fillMasks[run] >> bx);

                    x += runs[thisrun + 1];
                }
            }

            Debug.Assert(x == lastx);
        }

        /*
        * Find a span of ones or zeros using the supplied
        * table.  The ``base'' of the bit string is supplied
        * along with the start+end bit indices.
        */
        private static int find0span(byte[] bp, int bpOffset, int bs, int be)
        {
            int offset = bpOffset + (bs >> 3);

            /*
             * Check partial byte on lhs.
             */
            int bits = be - bs;
            int n = bs & 7;
            int span = 0;
            if (bits > 0 && n != 0)
            {
                span = m_zeroruns[(bp[offset] << n) & 0xff];

                if (span > 8 - n)
                {
                    /* table value too generous */
                    span = 8 - n;
                }

                if (span > bits)
                {
                    /* constrain span to bit range */
                    span = bits;
                }

                if (n + span < 8)
                {
                    /* doesn't extend to edge of byte */
                    return span;
                }

                bits -= span;
                offset++;
            }

            if (bits >= (2 * 8 * sizeof(int)))
            {
                /*
                 * Align to longword boundary and check longwords.
                 */
                while (!isLongAligned(offset))
                {
                    if (bp[offset] != 0x00)
                        return (span + m_zeroruns[bp[offset]]);

                    span += 8;
                    bits -= 8;
                    offset++;
                }

                while (bits >= 8 * sizeof(int))
                {
                    bool allZeros = true;
                    for (int i = 0; i < sizeof(int); i++)
                    {
                        if (bp[offset + i] != 0)
                        {
                            allZeros = false;
                            break;
                        }
                    }

                    if (allZeros)
                    {
                        span += 8 * sizeof(int);
                        bits -= 8 * sizeof(int);
                        offset += sizeof(int);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            /*
             * Scan full bytes for all 0's.
             */
            while (bits >= 8)
            {
                if (bp[offset] != 0x00)
                {
                    /* end of run */
                    return (span + m_zeroruns[bp[offset]]);
                }

                span += 8;
                bits -= 8;
                offset++;
            }

            /*
             * Check partial byte on rhs.
             */
            if (bits > 0)
            {
                n = m_zeroruns[bp[offset]];
                span += (n > bits ? bits : n);
            }

            return span;
        }

        private static int find1span(byte[] bp, int bpOffset, int bs, int be)
        {
            int offset = bpOffset + (bs >> 3);

            /*
             * Check partial byte on lhs.
             */
            int n = bs & 7;
            int span = 0;
            int bits = be - bs;
            if (bits > 0 && n != 0)
            {
                span = m_oneruns[(bp[offset] << n) & 0xff];
                if (span > 8 - n)
                {
                    /* table value too generous */
                    span = 8 - n;
                }

                if (span > bits)
                {
                    /* constrain span to bit range */
                    span = bits;
                }

                if (n + span < 8)
                {
                    /* doesn't extend to edge of byte */
                    return (span);
                }

                bits -= span;
                offset++;
            }

            if (bits >= (2 * 8 * sizeof(int)))
            {
                /*
                 * Align to longword boundary and check longwords.
                 */
                while (!isLongAligned(offset))
                {
                    if (bp[offset] != 0xff)
                        return (span + m_oneruns[bp[offset]]);

                    span += 8;
                    bits -= 8;
                    offset++;
                }

                while (bits >= 8 * sizeof(int))
                {
                    bool allOnes = true;
                    for (int i = 0; i < sizeof(int); i++)
                    {
                        if (bp[offset + i] != 0xff)
                        {
                            allOnes = false;
                            break;
                        }
                    }

                    if (allOnes)
                    {
                        span += 8 * sizeof(int);
                        bits -= 8 * sizeof(int);
                        offset += sizeof(int);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            /*
             * Scan full bytes for all 1's.
             */
            while (bits >= 8)
            {
                if (bp[offset] != 0xff)
                {
                    /* end of run */
                    return (span + m_oneruns[bp[offset]]);
                }

                span += 8;
                bits -= 8;
                offset++;
            }

            /*
             * Check partial byte on rhs.
             */
            if (bits > 0)
            {
                n = m_oneruns[bp[offset]];
                span += (n > bits ? bits : n);
            }

            return span;
        }

        /*
        * Return the offset of the next bit in the range
        * [bs..be] that is different from the specified
        * color.  The end, be, is returned if no such bit
        * exists.
        */
        private static int finddiff(byte[] bp, int bpOffset, int _bs, int _be, int _color)
        {
            if (_color != 0)
                return (_bs + find1span(bp, bpOffset, _bs, _be));

            return (_bs + find0span(bp, bpOffset, _bs, _be));
        }

        /*
        * Like finddiff, but also check the starting bit
        * against the end in case start > end.
        */
        private static int finddiff2(byte[] bp, int bpOffset, int _bs, int _be, int _color)
        {
            if (_bs < _be)
                return finddiff(bp, bpOffset, _bs, _be, _color);

            return _be;
        }

        /*
        * Group 3 and Group 4 Decoding.
        */

        /*
        * The following macros define the majority of the G3/G4 decoder
        * algorithm using the state tables defined elsewhere.  To build
        * a decoder you need some setup code and some glue code. Note
        * that you may also need/want to change the way the NeedBits*
        * macros get input data if, for example, you know the data to be
        * decoded is properly aligned and oriented (doing so before running
        * the decoder can be a big performance win).
        *
        * Consult the decoder in the TIFF library for an idea of what you
        * need to define and setup to make use of these definitions.
        *
        * NB: to enable a debugging version of these macros define FAX3_DEBUG
        *     before including this file.  Trace output goes to stdout.
        */

        private bool EndOfData()
        {
            return (m_tif.m_rawcp >= m_tif.m_rawcc);
        }

        private int GetBits(int n)
        {
            return (m_data & ((1 << n) - 1));
        }

        private void ClrBits(int n)
        {
            m_bit -= n;
            m_data >>= n;
        }

        /*
        * Need <=8 or <=16 bits of input data.  Unlike viewfax we
        * cannot use/assume a word-aligned, properly bit swizzled
        * input data set because data may come from an arbitrarily
        * aligned, read-only source such as a memory-mapped file.
        * Note also that the viewfax decoder does not check for
        * running off the end of the input data buffer.  This is
        * possible for G3-encoded data because it prescans the input
        * data to count EOL markers, but can cause problems for G4
        * data.  In any event, we don't prescan and must watch for
        * running out of data since we can't permit the library to
        * scan past the end of the input data buffer.
        *
        * Finally, note that we must handle remaindered data at the end
        * of a strip specially.  The coder asks for a fixed number of
        * bits when scanning for the next code.  This may be more bits
        * than are actually present in the data stream.  If we appear
        * to run out of data but still have some number of valid bits
        * remaining then we makeup the requested amount with zeros and
        * return successfully.  If the returned data is incorrect then
        * we should be called again and get a premature EOF error;
        * otherwise we should get the right answer.
        */
        private bool NeedBits8(int n)
        {
            if (m_bit < n)
            {
                if (EndOfData())
                {
                    if (m_bit == 0)
                    {
                        /* no valid bits */
                        return false;
                    }

                    m_bit = n; /* pad with zeros */
                }
                else
                {
                    m_data |= m_bitmap[m_tif.m_rawdata[m_tif.m_rawcp]] << m_bit;
                    m_tif.m_rawcp++;
                    m_bit += 8;
                }
            }

            return true;
        }

        private bool NeedBits16(int n)
        {
            if (m_bit < n)
            {
                if (EndOfData())
                {
                    if (m_bit == 0)
                    {
                        /* no valid bits */
                        return false;
                    }

                    m_bit = n; /* pad with zeros */
                }
                else
                {
                    m_data |= m_bitmap[m_tif.m_rawdata[m_tif.m_rawcp]] << m_bit;
                    m_tif.m_rawcp++;
                    m_bit += 8;
                    if (m_bit < n)
                    {
                        if (EndOfData())
                        {
                            /* NB: we know BitsAvail is non-zero here */
                            m_bit = n; /* pad with zeros */
                        }
                        else
                        {
                            m_data |= m_bitmap[m_tif.m_rawdata[m_tif.m_rawcp]] << m_bit;
                            m_tif.m_rawcp++;
                            m_bit += 8;
                        }
                    }
                }
            }

            return true;
        }

        private bool LOOKUP8(out faxTableEntry TabEnt, int wid)
        {
            if (!NeedBits8(wid))
            {
                TabEnt = new faxTableEntry();
                return false;
            }

            TabEnt = faxTableEntry.FromArray(m_faxMainTable, GetBits(wid));
            ClrBits(TabEnt.Width);

            return true;
        }

        private bool LOOKUP16(out faxTableEntry TabEnt, int wid, bool useBlack)
        {
            if (!NeedBits16(wid))
            {
                TabEnt = new faxTableEntry();
                return false;
            }

            if (useBlack)
                TabEnt = faxTableEntry.FromArray(m_faxBlackTable, GetBits(wid));
            else
                TabEnt = faxTableEntry.FromArray(m_faxWhiteTable, GetBits(wid));

            ClrBits(TabEnt.Width);

            return true;
        }

        /*
        * Synchronize input decoding at the start of each
        * row by scanning for an EOL (if appropriate) and
        * skipping any trash data that might be present
        * after a decoding error.  Note that the decoding
        * done elsewhere that recognizes an EOL only consumes
        * 11 consecutive zero bits.  This means that if EOLcnt
        * is non-zero then we still need to scan for the final flag
        * bit that is part of the EOL code.
        */
        private bool SYNC_EOL()
        {
            if (m_EOLcnt == 0)
            {
                for ( ; ; )
                {
                    if (!NeedBits16(11))
                        return false;

                    if (GetBits(11) == 0)
                        break;

                    ClrBits(1);
                }
            }

            for ( ; ; )
            {
                if (!NeedBits8(8))
                    return false;

                if (GetBits(8) != 0)
                    break;

                ClrBits(8);
            }

            while (GetBits(1) == 0)
                ClrBits(1);

            ClrBits(1); /* EOL bit */
            m_EOLcnt = 0; /* reset EOL counter/flag */

            return true;
        }

        /*
        * Setup G3/G4-related compression/decompression state
        * before data is processed.  This routine is called once
        * per image -- it sets up different state based on whether
        * or not decoding or encoding is being done and whether
        * 1D- or 2D-encoded data is involved.
        */
        private bool setupState()
        {
            if (m_tif.m_dir.td_bitspersample != 1)
            {
                Tiff.ErrorExt(m_tif, m_tif.m_clientdata, m_tif.m_name,
                    "Bits/sample must be 1 for Group 3/4 encoding/decoding");
                return false;
            }

            /*
             * Calculate the scanline/tile widths.
             */
            int rowbytes = 0;
            int rowpixels = 0;
            if (m_tif.IsTiled())
            {
                rowbytes = m_tif.TileRowSize();
                rowpixels = m_tif.m_dir.td_tilewidth;
            }
            else
            {
                rowbytes = m_tif.ScanlineSize();
                rowpixels = m_tif.m_dir.td_imagewidth;
            }
            
            m_rowbytes = rowbytes;
            m_rowpixels = rowpixels;
            
            /*
             * Allocate any additional space required for decoding/encoding.
             */
            bool needsRefLine = ((m_groupoptions & GROUP3OPT.GROUP3OPT_2DENCODING) != 0 ||
                m_tif.m_dir.td_compression == COMPRESSION.COMPRESSION_CCITTFAX4);

            int nruns = needsRefLine ? 2 * Tiff.roundUp(rowpixels, 32) : rowpixels;
            nruns += 3;
            m_runs = new int [2 * nruns];
            m_curruns = 0;

            if (needsRefLine)
                m_refruns = nruns;
            else
                m_refruns = -1;
            
            if (m_tif.m_dir.td_compression == COMPRESSION.COMPRESSION_CCITTFAX3 && is2DEncoding())
            {
                /* NB: default is 1D routine */
                m_decoder = Decoder.useFax3_2DDecoder;
            }

            if (needsRefLine)
            {
                /* 2d encoding */
                /*
                 * 2d encoding requires a scanline
                 * buffer for the "reference line"; the
                 * scanline against which delta encoding
                 * is referenced.  The reference line must
                 * be initialized to be "white" (done elsewhere).
                 */
                m_refline = new byte [rowbytes + 1];
            }
            else
            {
                /* 1d encoding */
                m_refline = null;
            }

            return true;
        }

        /*
        * Routine for handling various errors/conditions.
        * Note how they are "glued into the decoder" by
        * overriding the definitions used by the decoder.
        */
        private void Fax3Unexpected(string module)
        {
            Tiff.ErrorExt(m_tif, m_tif.m_clientdata, module,
                "{0}: Bad code word at line {1} of {2} {3} (x {4})", 
                m_tif.m_name, m_line, m_tif.IsTiled() ? "tile" : "strip", 
                (m_tif.IsTiled() ? m_tif.m_curtile : m_tif.m_curstrip), m_a0);
        }

        private void Fax3Extension(string module)
        {
            Tiff.ErrorExt(m_tif, m_tif.m_clientdata, module,
                "{0}: Uncompressed data (not supported) at line {1} of {2} {3} (x {4})",
                m_tif.m_name, m_line, m_tif.IsTiled() ? "tile" : "strip", 
                (m_tif.IsTiled() ? m_tif.m_curtile : m_tif.m_curstrip), m_a0);
        }

        private void Fax3BadLength(string module)
        {
            Tiff.WarningExt(m_tif, m_tif.m_clientdata, module,
                "{0}: {1} at line {2} of {3} {4} (got {5}, expected {6})",
                m_tif.m_name, m_a0 < (int)m_rowpixels ? "Premature EOL" : "Line length mismatch",
                m_line, m_tif.IsTiled() ? "tile" : "strip", 
                (m_tif.IsTiled() ? m_tif.m_curtile : m_tif.m_curstrip), m_a0, m_rowpixels);
        }

        private void Fax3PrematureEOF(string module)
        {
            Tiff.WarningExt(m_tif, m_tif.m_clientdata, module,
                "{0}: Premature EOF at line {1} of {2} {3} (x {4})",
                m_tif.m_name, m_line, m_tif.IsTiled() ? "tile" : "strip", 
                (m_tif.IsTiled() ? m_tif.m_curtile : m_tif.m_curstrip), m_a0);
        }

        /*
        * Decode the requested amount of G3 1D-encoded data.
        */
        private bool Fax3Decode1D(byte[] buf, int occ)
        {
            const string module = "Fax3Decode1D";
    
            /* current row's run array */
            m_thisrun = m_curruns;
            int startOffset = 0;
            while (occ > 0)
            {
                m_a0 = 0;
                m_RunLength = 0;
                m_pa = m_thisrun;

                if (!SYNC_EOL())
                {
                    /* premature EOF */
                    CLEANUP_RUNS(module);
                }
                else
                {
                    bool expandSucceeded = EXPAND1D(module);
                    if (expandSucceeded)
                    {
                        fill(buf, startOffset, m_runs, m_thisrun, m_pa, m_rowpixels);
                        startOffset += m_rowbytes;
                        occ -= m_rowbytes;
                        m_line++;
                        continue;
                    }
                }

                /* premature EOF */
                fill(buf, startOffset, m_runs, m_thisrun, m_pa, m_rowpixels);
                return false;
            }

            return true;
        }

        /*
        * Decode the requested amount of G3 2D-encoded data.
        */
        private bool Fax3Decode2D(byte[] buf, int occ)
        {
            const string module = "Fax3Decode2D";
            int startOffset = 0;

            while (occ > 0)
            {
                m_a0 = 0;
                m_RunLength = 0;
                m_pa = m_curruns;
                m_thisrun = m_curruns;

                bool prematureEOF = false;
                if (!SYNC_EOL())
                    prematureEOF = true;

                if (!prematureEOF && !NeedBits8(1))
                    prematureEOF = true;

                if (!prematureEOF)
                {
                    int is1D = GetBits(1); /* 1D/2D-encoding tag bit */
                    ClrBits(1);
                    m_pb = m_refruns;
                    int b1 = m_runs[m_pb];
                    m_pb++; /* next change on prev line */

                    bool expandSucceeded = false;
                    if (is1D != 0)
                        expandSucceeded = EXPAND1D(module);
                    else
                        expandSucceeded = EXPAND2D(module, b1);

                    if (expandSucceeded)
                    {
                        fill(buf, startOffset, m_runs, m_thisrun, m_pa, m_rowpixels);
                        SETVALUE(0); /* imaginary change for reference */
                        SWAP(ref m_curruns, ref m_refruns);
                        startOffset += m_rowbytes;
                        occ -= m_rowbytes;
                        m_line++;
                        continue;
                    }
                }
                else
                {
                    /* premature EOF */
                    CLEANUP_RUNS(module);
                }

                /* premature EOF */
                fill(buf, startOffset, m_runs, m_thisrun, m_pa, m_rowpixels);
                return false;
            }

            return true;
        }

        /*
        * 1d-encode a row of pixels.  The encoding is
        * a sequence of all-white or all-black spans
        * of pixels encoded with Huffman codes.
        */
        private bool Fax3Encode1DRow()
        {
            int bs = 0;
            for ( ; ; )
            {
                int span = find0span(m_bp, m_bpPos, bs, m_rowpixels); /* white span */
                putspan(span, false);
                bs += span;
                if (bs >= m_rowpixels)
                    break;

                span = find1span(m_bp, m_bpPos, bs, m_rowpixels); /* black span */
                putspan(span, true);
                bs += span;
                if (bs >= m_rowpixels)
                    break;
            }

            if ((m_mode & (FAXMODE.FAXMODE_BYTEALIGN | FAXMODE.FAXMODE_WORDALIGN)) != 0)
            {
                if (m_bit != 8)
                {
                    /* byte-align */
                    flushBits();
                }

                if ((m_mode & FAXMODE.FAXMODE_WORDALIGN) != 0 && !isUint16Aligned(m_tif.m_rawcp))
                    flushBits();
            }

            return true;
        }

        /*
        * 2d-encode a row of pixels.  Consult the CCITT
        * documentation for the algorithm.
        */
        private bool Fax3Encode2DRow()
        {
            int a0 = 0;
            int a1 = (Fax3Encode2DRow_Pixel(m_bp, m_bpPos, 0) != 0 ? 0 : finddiff(m_bp, m_bpPos, 0, m_rowpixels, 0));
            int b1 = (Fax3Encode2DRow_Pixel(m_refline, 0, 0) != 0 ? 0 : finddiff(m_refline, 0, 0, m_rowpixels, 0));

            for (; ; )
            {
                int b2 = finddiff2(m_refline, 0, b1, m_rowpixels, Fax3Encode2DRow_Pixel(m_refline, 0, b1));
                if (b2 >= a1)
                {
                    int d = b1 - a1;
                    if (!(-3 <= d && d <= 3))
                    {
                        /* horizontal mode */
                        int a2 = finddiff2(m_bp, m_bpPos, a1, m_rowpixels, Fax3Encode2DRow_Pixel(m_bp, m_bpPos, a1));
                        putcode(m_horizcode);

                        if (a0 + a1 == 0 || Fax3Encode2DRow_Pixel(m_bp, m_bpPos, a0) == 0)
                        {
                            putspan(a1 - a0, false);
                            putspan(a2 - a1, true);
                        }
                        else
                        {
                            putspan(a1 - a0, true);
                            putspan(a2 - a1, false);
                        }

                        a0 = a2;
                    }
                    else
                    {
                        /* vertical mode */
                        putcode(m_vcodes[d + 3]);
                        a0 = a1;
                    }
                }
                else
                {
                    /* pass mode */
                    putcode(m_passcode);
                    a0 = b2;
                }

                if (a0 >= m_rowpixels)
                    break;

                a1 = finddiff(m_bp, m_bpPos, a0, m_rowpixels, Fax3Encode2DRow_Pixel(m_bp, m_bpPos, a0));

                int color = Fax3Encode2DRow_Pixel(m_bp, m_bpPos, a0);
                if (color == 0)
                    color = 1;
                else
                    color = 0;

                b1 = finddiff(m_refline, 0, a0, m_rowpixels, color);
                b1 = finddiff(m_refline, 0, b1, m_rowpixels, Fax3Encode2DRow_Pixel(m_bp, m_bpPos, a0));
            }

            return true;
        }

        private static int Fax3Encode2DRow_Pixel(byte[] buf, int bufOffset, int ix)
        {
            return ((buf[bufOffset + (ix >> 3)] >> (7 - (ix & 7))) & 1);
        }

        /*
        * Encode a buffer of pixels.
        */
        private bool Fax3Encode(byte[] bp, int cc)
        {
            m_bp = bp;
            m_bpPos = 0;

            while (cc > 0)
            {
                if ((m_mode & FAXMODE.FAXMODE_NOEOL) == 0)
                    Fax3PutEOL();

                if (is2DEncoding())
                {
                    if (m_encoder == Fax3Encoder.useFax1DEncoder)
                    {
                        if (!Fax3Encode1DRow())
                            return false;

                        m_encoder = Fax3Encoder.useFax2DEncoder;
                    }
                    else
                    {
                        if (!Fax3Encode2DRow())
                            return false;

                        m_k--;
                    }

                    if (m_k == 0)
                    {
                        m_encoder = Fax3Encoder.useFax1DEncoder;
                        m_k = m_maxk - 1;
                    }
                    else
                        Array.Copy(m_bp, m_bpPos, m_refline, 0, m_rowbytes);
                }
                else
                {
                    if (!Fax3Encode1DRow())
                        return false;
                }

                m_bpPos += m_rowbytes;
                cc -= m_rowbytes;
            }

            return true;
        }

        private bool Fax3PostEncode()
        {
            if (m_bit != 8)
                flushBits();

            return true;
        }

        private void InitCCITTFax3()
        {
            /*
            * Merge codec-specific tag information and
            * override parent get/set field methods.
            */
            m_tif.MergeFieldInfo(m_faxFieldInfo, m_faxFieldInfo.Length);

            /*
             * Allocate state block so tag methods have storage to record values.
             */
            m_rw_mode = m_tif.m_mode;

            m_parentTagMethods = m_tif.m_tagmethods;
            m_tif.m_tagmethods = m_tagMethods;
            
            m_groupoptions = 0;
            m_recvparams = 0;
            m_subaddress = null;
            m_faxdcs = null;

            if (m_rw_mode == Tiff.O_RDONLY)
            {
                /* FIXME: improve for in place update */
                m_tif.m_flags |= Tiff.TIFF_NOBITREV;
                /* decoder does bit reversal */
            }

            m_runs = null;
            m_tif.SetField(TIFFTAG.TIFFTAG_FAXFILLFUNC, new FaxFillFunc(fax3FillRuns));
            m_refline = null;

            m_decoder = Decoder.useFax3_1DDecoder;
            m_encodingFax4 = false;
        }

        private bool TIFFInitCCITTFax3()
        {
            InitCCITTFax3();
            m_tif.MergeFieldInfo(m_fax3FieldInfo, m_fax3FieldInfo.Length);

            /*
             * The default format is Class/F-style w/o RTC.
             */
            return m_tif.SetField(TIFFTAG.TIFFTAG_FAXMODE, FAXMODE.FAXMODE_CLASSF);
        }

        /*
        * CCITT Group 3 FAX Encoding.
        */
        private void flushBits()
        {
            if (m_tif.m_rawcc >= m_tif.m_rawdatasize)
                m_tif.flushData1();

            m_tif.m_rawdata[m_tif.m_rawcp] = (byte)m_data;
            m_tif.m_rawcp++;
            m_tif.m_rawcc++;
            m_data = 0;
            m_bit = 8;
        }
        
        /*
        * Write a variable-length bit-value to
        * the output stream.  Values are
        * assumed to be at most 16 bits.
        */
        private void putBits(int bits, int length)
        {
            while (length > m_bit)
            {
                m_data |= bits >> (length - m_bit);
                length -= m_bit;
                flushBits();
            }

            m_data |= (bits & m_msbmask[length]) << (m_bit - length);
            m_bit -= length;
            if (m_bit == 0)
                flushBits();
        }
        
        /*
        * Write a code to the output stream.
        */
        private void putcode(tableEntry te)
        {
            putBits(te.code, te.length);
        }

        /*
        * Write the sequence of codes that describes
        * the specified span of zero's or one's.  The
        * appropriate table that holds the make-up and
        * terminating codes is supplied.
        */
        private void putspan(int span, bool useBlack)
        {
            short[] entries = null;
            if (useBlack)
                entries = m_faxBlackCodes;
            else
                entries = m_faxWhiteCodes;

            tableEntry te = tableEntry.FromArray(entries, 63 + (2560 >> 6));
            while (span >= 2624)
            {
                putBits(te.code, te.length);
                span -= te.runlen;
            }

            if (span >= 64)
            {
                te = tableEntry.FromArray(entries, 63 + (span >> 6));
                Debug.Assert(te.runlen == 64 * (span >> 6));
                putBits(te.code, te.length);
                span -= te.runlen;
            }

            te = tableEntry.FromArray(entries, span);
            putBits(te.code, te.length);
        }

        /*
        * Write an EOL code to the output stream.  The zero-fill
        * logic for byte-aligning encoded scanlines is handled
        * here.  We also handle writing the tag bit for the next
        * scanline when doing 2d encoding.
        */
        private void Fax3PutEOL()
        {
            if ((m_groupoptions & GROUP3OPT.GROUP3OPT_FILLBITS) != 0)
            {
                /*
                 * Force bit alignment so EOL will terminate on
                 * a byte boundary.  That is, force the bit alignment
                 * to 16-12 = 4 before putting out the EOL code.
                 */
                int align = 8 - 4;
                if (align != m_bit)
                {
                    if (align > m_bit)
                        align = m_bit + (8 - align);
                    else
                        align = m_bit - align;

                    putBits(0, align);
                }
            }

            int code = EOL_CODE;
            int length = 12;
            if (is2DEncoding())
            {
                code = (code << 1);
                if (m_encoder == Fax3Encoder.useFax1DEncoder)
                    code++;

                length++;
            }
            
            putBits(code, length);
        }

        /*
        * Append a run to the run length array for the
        * current row and reset decoding state.
        */
        private void SETVALUE(int x)
        {
            m_runs[m_pa] = m_RunLength + x;
            m_pa++;
            m_a0 += x;
            m_RunLength = 0;
        }

        /*
        * Cleanup the array of runs after decoding a row.
        * We adjust final runs to insure the user buffer is not
        * overwritten and/or undecoded area is white filled.
        */
        private void CLEANUP_RUNS(string module)
        {
            if (m_RunLength != 0)
                SETVALUE(0);

            if (m_a0 != m_rowpixels)
            {
                Fax3BadLength(module);

                while (m_a0 > m_rowpixels && m_pa > m_thisrun)
                {
                    m_pa--;
                    m_a0 -= m_runs[m_pa];
                }

                if (m_a0 < m_rowpixels)
                {
                    if (m_a0 < 0)
                        m_a0 = 0;

                    if (((m_pa - m_thisrun) & 1) != 0)
                        SETVALUE(0);

                    SETVALUE(m_rowpixels - m_a0);
                }
                else if (m_a0 > m_rowpixels)
                {
                    SETVALUE(m_rowpixels);
                    SETVALUE(0);
                }
            }
        }

        private void handlePrematureEOFinExpand2D(string module)
        {
            Fax3PrematureEOF(module);
            CLEANUP_RUNS(module);
        }

        /*
        * Decode a line of 1D-encoded data.
        */
        private bool EXPAND1D(string module)
        {
            faxTableEntry TabEnt;
            bool decodingDone = false;
            bool whiteDecodingDone = false;
            bool blackDecodingDone = false;

            for ( ; ; )
            {
                for ( ; ; )
                {
                    if (!LOOKUP16(out TabEnt, 12, false))
                    {
                        Fax3PrematureEOF(module);
                        CLEANUP_RUNS(module);
                        return false;
                    }

                    switch (TabEnt.State)
                    {
                        case S_EOL:
                            m_rowpixels = 1;
                            decodingDone = true;
                            break;

                        case S_TermW:
                            SETVALUE(TabEnt.Param);
                            whiteDecodingDone = true;
                            break;

                        case S_MakeUpW:
                        case S_MakeUp:
                            m_a0 += TabEnt.Param;
                            m_RunLength += TabEnt.Param;
                            break;

                        default:
                            /* "WhiteTable" */
                            Fax3Unexpected(module);
                            decodingDone = true;
                            break;
                    }

                    if (decodingDone || whiteDecodingDone)
                        break;
                }

                if (decodingDone)
                    break;

                if (m_a0 >= m_rowpixels)
                    break;

                for ( ; ; )
                {
                    if (!LOOKUP16(out TabEnt, 13, true))
                    {
                        Fax3PrematureEOF(module);
                        CLEANUP_RUNS(module);
                        return false;
                    }

                    switch (TabEnt.State)
                    {
                        case S_EOL:
                            m_EOLcnt = 1;
                            decodingDone = true;
                            break;

                        case S_TermB:
                            SETVALUE(TabEnt.Param);
                            blackDecodingDone = true;
                            break;

                        case S_MakeUpB:
                        case S_MakeUp:
                            m_a0 += TabEnt.Param;
                            m_RunLength += TabEnt.Param;
                            break;

                        default:
                            /* "BlackTable" */
                            Fax3Unexpected(module);
                            decodingDone = true;
                            break;
                    }

                    if (decodingDone || blackDecodingDone)
                        break;
                }

                if (decodingDone)
                    break;

                if (m_a0 >= m_rowpixels)
                    break;

                if (m_runs[m_pa - 1] == 0 && m_runs[m_pa - 2] == 0)
                    m_pa -= 2;

                whiteDecodingDone = false;
                blackDecodingDone = false;
            }

            CLEANUP_RUNS(module);
            return true;
        }

        /*
        * Expand a row of 2D-encoded data.
        */
        private bool EXPAND2D(string module, int b1)
        {
            faxTableEntry TabEnt;
            bool decodingDone = false;

            while (m_a0 < m_rowpixels)
            {
                if (!LOOKUP8(out TabEnt, 7))
                {
                    handlePrematureEOFinExpand2D(module);
                    return false;
                }

                switch (TabEnt.State)
                {
                    case S_Pass:
                        CHECK_b1(ref b1);
                        b1 += m_runs[m_pb];
                        m_pb++;
                        m_RunLength += b1 - m_a0;
                        m_a0 = b1;
                        b1 += m_runs[m_pb];
                        m_pb++;
                        break;

                    case S_Horiz:
                        if (((m_pa - m_thisrun) & 1) != 0)
                        {
                            for ( ; ; )
                            {
                                /* black first */
                                if (!LOOKUP16(out TabEnt, 13, true))
                                {
                                    handlePrematureEOFinExpand2D(module);
                                    return false;
                                }

                                bool doneWhite2d = false;
                                switch (TabEnt.State)
                                {
                                    case S_TermB:
                                        SETVALUE(TabEnt.Param);
                                        doneWhite2d = true;
                                        break;

                                    case S_MakeUpB:
                                    case S_MakeUp:
                                        m_a0 += TabEnt.Param;
                                        m_RunLength += TabEnt.Param;
                                        break;

                                    default:
                                        /* "BlackTable" */
                                        Fax3Unexpected(module);
                                        decodingDone = true;
                                        break;
                                }

                                if (doneWhite2d || decodingDone)
                                    break;
                            }

                            if (decodingDone)
                                break;

                            for ( ; ; )
                            {
                                /* then white */
                                if (!LOOKUP16(out TabEnt, 12, false))
                                {
                                    handlePrematureEOFinExpand2D(module);
                                    return false;
                                }

                                bool doneBlack2d = false;
                                switch (TabEnt.State)
                                {
                                    case S_TermW:
                                        SETVALUE(TabEnt.Param);
                                        doneBlack2d = true;
                                        break;

                                    case S_MakeUpW:
                                    case S_MakeUp:
                                        m_a0 += TabEnt.Param;
                                        m_RunLength += TabEnt.Param;
                                        break;

                                    default:
                                        /* "WhiteTable" */
                                        Fax3Unexpected(module);
                                        decodingDone = true;
                                        break;
                                }

                                if (doneBlack2d || decodingDone)
                                    break;
                            }

                            if (decodingDone)
                                break;
                        }
                        else
                        {
                            for ( ; ; )
                            {
                                /* white first */
                                if (!LOOKUP16(out TabEnt, 12, false))
                                {
                                    handlePrematureEOFinExpand2D(module);
                                    return false;
                                }

                                bool doneWhite2d = false;
                                switch (TabEnt.State)
                                {
                                    case S_TermW:
                                        SETVALUE(TabEnt.Param);
                                        doneWhite2d = true;
                                        break;

                                    case S_MakeUpW:
                                    case S_MakeUp:
                                        m_a0 += TabEnt.Param;
                                        m_RunLength += TabEnt.Param;
                                        break;

                                    default:
                                        /* "WhiteTable" */
                                        Fax3Unexpected(module);
                                        decodingDone = true;
                                        break;
                                }

                                if (doneWhite2d || decodingDone)
                                    break;
                            }

                            if (decodingDone)
                                break;

                            for ( ; ; )
                            {
                                /* then black */
                                if (!LOOKUP16(out TabEnt, 13, true))
                                {
                                    handlePrematureEOFinExpand2D(module);
                                    return false;
                                }

                                bool doneBlack2d = false;
                                switch (TabEnt.State)
                                {
                                    case S_TermB:
                                        SETVALUE(TabEnt.Param);
                                        doneBlack2d = true;
                                        break;

                                    case S_MakeUpB:
                                    case S_MakeUp:
                                        m_a0 += TabEnt.Param;
                                        m_RunLength += TabEnt.Param;
                                        break;

                                    default:
                                        /* "BlackTable" */
                                        Fax3Unexpected(module);
                                        decodingDone = true;
                                        break;
                                }

                                if (doneBlack2d || decodingDone)
                                    break;
                            }
                        }

                        if (decodingDone)
                            break;

                        CHECK_b1(ref b1);
                        break;

                    case S_V0:
                        CHECK_b1(ref b1);
                        SETVALUE(b1 - m_a0);
                        b1 += m_runs[m_pb];
                        m_pb++;
                        break;

                    case S_VR:
                        CHECK_b1(ref b1);
                        SETVALUE(b1 - m_a0 + TabEnt.Param);
                        b1 += m_runs[m_pb];
                        m_pb++;
                        break;

                    case S_VL:
                        CHECK_b1(ref b1);
                        SETVALUE(b1 - m_a0 - TabEnt.Param);
                        m_pb--;
                        b1 -= m_runs[m_pb];
                        break;

                    case S_Ext:
                        m_runs[m_pa] = m_rowpixels - m_a0;
                        m_pa++;
                        Fax3Extension(module);
                        decodingDone = true;
                        break;

                    case S_EOL:
                        m_runs[m_pa] = m_rowpixels - m_a0;
                        m_pa++;

                        if (!NeedBits8(4))
                        {
                            handlePrematureEOFinExpand2D(module);
                            return false;
                        }

                        if (GetBits(4) != 0)
                        {
                            /* "EOL" */
                            Fax3Unexpected(module);
                        }

                        ClrBits(4);
                        m_EOLcnt = 1;
                        decodingDone = true;
                        break;

                    default:
                        Fax3Unexpected(module);
                        decodingDone = true;
                        break;
                }
            }

            if (!decodingDone && m_RunLength != 0)
            {
                if (m_RunLength + m_a0 < (int)m_rowpixels)
                {
                    /* expect a final V0 */
                    if (!NeedBits8(1))
                    {
                        handlePrematureEOFinExpand2D(module);
                        return false;
                    }

                    if (GetBits(1) == 0)
                    {
                        /* "MainTable" */
                        Fax3Unexpected(module);
                        decodingDone = true;
                    }

                    if (!decodingDone)
                        ClrBits(1);
                }

                if (!decodingDone)
                    SETVALUE(0);
            }

            CLEANUP_RUNS(module);
            return true;
        }

        /*
        * CCITT Group 3 1-D Modified Huffman RLE Compression Support.
        * (Compression algorithms 2 and 32771)
        */

        private bool TIFFInitCCITTRLE()
        {
            /* reuse G3 support */
            InitCCITTFax3();

            m_decoder = Decoder.useFax3RLEDecoder;

            /*
             * Suppress RTC+EOLs when encoding and byte-align data.
             */
            return m_tif.SetField(TIFFTAG.TIFFTAG_FAXMODE, 
                FAXMODE.FAXMODE_NORTC | FAXMODE.FAXMODE_NOEOL | FAXMODE.FAXMODE_BYTEALIGN);
        }

        private bool TIFFInitCCITTRLEW()
        {
            /* reuse G3 support */
            InitCCITTFax3();

            m_decoder = Decoder.useFax3RLEDecoder;

            /*
             * Suppress RTC+EOLs when encoding and word-align data.
             */
            return m_tif.SetField(TIFFTAG.TIFFTAG_FAXMODE, 
                FAXMODE.FAXMODE_NORTC | FAXMODE.FAXMODE_NOEOL | FAXMODE.FAXMODE_WORDALIGN);
        }

        /*
        * Decode the requested amount of RLE-encoded data.
        */
        private bool Fax3DecodeRLE(byte[] buf, int occ)
        {
            const string module = "Fax3DecodeRLE";

            int thisrun = m_curruns; /* current row's run array */
            int startOffset = 0;

            while (occ > 0)
            {
                m_a0 = 0;
                m_RunLength = 0;
                m_pa = thisrun;

                bool expandSucceeded = EXPAND1D(module);
                if (expandSucceeded)
                {
                    fill(buf, startOffset, m_runs, thisrun, m_pa, m_rowpixels);

                    /*
                     * Cleanup at the end of the row.
                     */
                    if ((m_mode & FAXMODE.FAXMODE_BYTEALIGN) != 0)
                    {
                        int n = m_bit - (m_bit & ~7);
                        ClrBits(n);
                    }
                    else if ((m_mode & FAXMODE.FAXMODE_WORDALIGN) != 0)
                    {
                        int n = m_bit - (m_bit & ~15);
                        ClrBits(n);
                        if (m_bit == 0 && !isUint16Aligned(m_tif.m_rawcp))
                            m_tif.m_rawcp++;
                    }

                    startOffset += m_rowbytes;
                    occ -= m_rowbytes;
                    m_line++;
                    continue;
                }

                /* premature EOF */
                fill(buf, startOffset, m_runs, thisrun, m_pa, m_rowpixels);
                return false;
            }

            return true;
        }

        /*
        * CCITT Group 4 (T.6) Facsimile-compatible
        * Compression Scheme Support.
        */

        private bool TIFFInitCCITTFax4()
        {
            /* reuse G3 support */
            InitCCITTFax3();

            m_tif.MergeFieldInfo(m_fax4FieldInfo, m_fax4FieldInfo.Length);

            m_decoder = Decoder.useFax4Decoder;
            m_encodingFax4 = true;

            /*
             * Suppress RTC at the end of each strip.
             */
            return m_tif.SetField(TIFFTAG.TIFFTAG_FAXMODE, FAXMODE.FAXMODE_NORTC);
        }

        /*
        * Decode the requested amount of G4-encoded data.
        */
        private bool Fax4Decode(byte[] buf, int occ)
        {
            const string module = "Fax4Decode";
            int startOffset = 0;

            while (occ > 0)
            {
                m_a0 = 0;
                m_RunLength = 0;
                m_thisrun = m_curruns;
                m_pa = m_curruns;
                m_pb = m_refruns;
                int b1 = m_runs[m_pb];
                m_pb++; /* next change on prev line */

                bool expandSucceeded = EXPAND2D(module, b1);
                if (expandSucceeded && m_EOLcnt != 0)
                    expandSucceeded = false;

                if (expandSucceeded)
                {
                    fill(buf, startOffset, m_runs, m_thisrun, m_pa, m_rowpixels);
                    SETVALUE(0); /* imaginary change for reference */
                    SWAP(ref m_curruns, ref m_refruns);
                    startOffset += m_rowbytes;
                    occ -= m_rowbytes;
                    m_line++;
                    continue;
                }

                NeedBits16(13);
                ClrBits(13);
                fill(buf, startOffset, m_runs, m_thisrun, m_pa, m_rowpixels);
                return false;
            }

            return true;
        }

        /*
        * Encode the requested amount of data.
        */
        private bool Fax4Encode(byte[] bp, int cc)
        {
            m_bp = bp;
            m_bpPos = 0;

            while (cc > 0)
            {
                if (!Fax3Encode2DRow())
                    return false;

                Array.Copy(m_bp, m_bpPos, m_refline, 0, m_rowbytes);
                m_bpPos += m_rowbytes;
                cc -= m_rowbytes;
            }

            return true;
        }

        private bool Fax4PostEncode()
        {
            /* terminate strip w/ EOFB */
            putBits(EOL_CODE, 12);
            putBits(EOL_CODE, 12);

            if (m_bit != 8)
                flushBits();

            return true;
        }
    }
}
