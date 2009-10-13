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
 * Directory Write Support Routines.
 */

using System;
using System.Collections.Generic;
using System.Text;

using BitMiracle.LibTiff.Internal;
using System.Diagnostics;

namespace BitMiracle.LibTiff
{
    public partial class Tiff
    {
        private uint insertData(UInt16 type, uint v)
        {
            return ((uint)(m_header.tiff_magic == TIFF_BIGENDIAN ? (v & m_typemask[type]) << m_typeshift[type] : v & m_typemask[type]));
        }

        private static void resetFieldBit(uint[] fields, short f)
        {
            fields[f / 32] &= ~BITn(f);
        }

        private static bool fieldSet(uint[] fields, short f)
        {
            return ((fields[f / 32] & BITn(f)) != 0);
        }

        private bool writeRational(TiffDataType type, TIFFTAG tag, ref TiffDirEntry dir, float v)
        {
            dir.tdir_tag = tag;
            dir.tdir_type = type;
            dir.tdir_count = 1;

            float[] a = new float[1];
            a[0] = v;
            if (!writeRationalArray(ref dir, a))
                return false;

            return true;
        }

        private bool writeRationalPair(TiffDirEntry[] entries, int dirOffset, TiffDataType type, TIFFTAG tag1, float v1, TIFFTAG tag2, float v2)
        {
            if (!writeRational(type, tag1, ref entries[dirOffset], v1))
                return false;

            if (!writeRational(type, tag2, ref entries[dirOffset + 1], v2))
                return false;

            return true;
        }

        /*
        * Write the contents of the current directory
        * to the specified file.  This routine doesn't
        * handle overwriting a directory with auxiliary
        * storage that's been changed.
        */
        private bool writeDirectory(bool done)
        {
            if (m_mode == O_RDONLY)
                return true;

            /*
             * Clear write state so that subsequent images with
             * different characteristics get the right buffers
             * setup for them.
             */
            if (done)
            {
                if ((m_flags & TIFF_POSTENCODE) != 0)
                {
                    m_flags &= ~TIFF_POSTENCODE;
                    if (!m_currentCodec.tif_postencode())
                    {
                        ErrorExt(this, m_clientdata, m_name, "Error post-encoding before directory write");
                        return false;
                    }
                }

                m_currentCodec.tif_close(); /* shutdown encoder */
                
                /*
                 * Flush any data that might have been written
                 * by the compression close+cleanup routines.
                 */
                if (m_rawcc > 0 && (m_flags & TIFF_BEENWRITING) != 0 && !flushData1())
                {
                    ErrorExt(this, m_clientdata, m_name, "Error flushing data before directory write");
                    return false;
                }

                if ((m_flags & TIFF_MYBUFFER) != 0 && m_rawdata != null)
                {
                    m_rawdata = null;
                    m_rawcc = 0;
                    m_rawdatasize = 0;
                }

                m_flags &= ~(TIFF_BEENWRITING | TIFF_BUFFERSETUP);
            }

            /*
             * Size the directory so that we can calculate
             * offsets for the data items that aren't kept
             * in-place in each field.
             */
            int nfields = 0;
            for (int b = 0; b <= FIELD.FIELD_LAST; b++)
            {
                if (fieldSet(b) && b != FIELD.FIELD_CUSTOM)
                    nfields += (b < FIELD.FIELD_SUBFILETYPE ? 2 : 1);
            }

            nfields += m_dir.td_customValueCount;
            int dirsize = nfields * TiffDirEntry.SizeInBytes;
            TiffDirEntry[] data = new TiffDirEntry [nfields];
            if (data == null)
            {
                ErrorExt(this, m_clientdata, m_name, "Cannot write directory, out of space");
                return false;
            }

            /*
             * Directory hasn't been placed yet, put
             * it at the end of the file and link it
             * into the existing directory structure.
             */
            if (m_diroff == 0 && !linkDirectory())
            {
                return false;
            }

            m_dataoff = m_diroff + sizeof(UInt16) + dirsize + sizeof(uint);
            if ((m_dataoff & 1) != 0)
                m_dataoff++;

            seekFile(m_dataoff, SEEK_SET);
            m_curdir++;
            int dir = 0;

            /*
             * Setup external form of directory
             * entries and write data items.
             */
            uint[] fields = new uint[FIELD.FIELD_SETLONGS];
            Array.Copy(m_dir.td_fieldsset, fields, FIELD.FIELD_SETLONGS);

            /*
             * Write out ExtraSamples tag only if
             * extra samples are present in the data.
             */
            if (fieldSet(fields, FIELD.FIELD_EXTRASAMPLES) && m_dir.td_extrasamples == 0)
            {
                resetFieldBit(fields, FIELD.FIELD_EXTRASAMPLES);
                nfields--;
                dirsize -= TiffDirEntry.SizeInBytes;
            } /*XXX*/

            for (int fi = 0, nfi = m_nfields; nfi > 0; nfi--, fi++)
            {
                TiffFieldInfo fip = m_fieldinfo[fi];

                /*
                 * For custom fields, we test to see if the custom field
                 * is set or not.  For normal fields, we just use the
                 * fieldSet test. 
                 */
                if (fip.field_bit == FIELD.FIELD_CUSTOM)
                {
                    bool is_set = false;
                    for (int ci = 0; ci < m_dir.td_customValueCount; ci++)
                        is_set |= (m_dir.td_customValues[ci].info == fip);

                    if (!is_set)
                        continue;
                }
                else if (!fieldSet(fields, fip.field_bit))
                    continue;


                /*
                 * Handle other fields.
                 */
                TIFFTAG tag = FIELD.FIELD_IGNORE;
                switch (fip.field_bit)
                {
                    case FIELD.FIELD_STRIPOFFSETS:
                        /*
                         * We use one field bit for both strip and tile
                         * offsets, and so must be careful in selecting
                         * the appropriate field descriptor (so that tags
                         * are written in sorted order).
                         */
                        tag = IsTiled() ? TIFFTAG.TIFFTAG_TILEOFFSETS : TIFFTAG.TIFFTAG_STRIPOFFSETS;
                        if (tag != fip.field_tag)
                            continue;

                        data[dir].tdir_tag = tag;
                        data[dir].tdir_type = TiffDataType.TIFF_LONG;
                        data[dir].tdir_count = m_dir.td_nstrips;
                        if (!writeLongArray(ref data[dir], m_dir.td_stripoffset))
                            return false;

                        break;
                    case FIELD.FIELD_STRIPBYTECOUNTS:
                        /*
                         * We use one field bit for both strip and tile
                         * byte counts, and so must be careful in selecting
                         * the appropriate field descriptor (so that tags
                         * are written in sorted order).
                         */
                        tag = IsTiled() ? TIFFTAG.TIFFTAG_TILEBYTECOUNTS: TIFFTAG.TIFFTAG_STRIPBYTECOUNTS;
                        if (tag != fip.field_tag)
                            continue;

                        data[dir].tdir_tag = tag;
                        data[dir].tdir_type = TiffDataType.TIFF_LONG;
                        data[dir].tdir_count = m_dir.td_nstrips;
                        if (!writeLongArray(ref data[dir], m_dir.td_stripbytecount))
                            return false;

                        break;
                    case FIELD.FIELD_ROWSPERSTRIP:
                        setupShortLong(TIFFTAG.TIFFTAG_ROWSPERSTRIP, ref data[dir], m_dir.td_rowsperstrip);
                        break;
                    case FIELD.FIELD_COLORMAP:
                        if (!writeShortTable(TIFFTAG.TIFFTAG_COLORMAP, ref data[dir], 3, m_dir.td_colormap))
                            return false;

                        break;
                    case FIELD.FIELD_IMAGEDIMENSIONS:
                        setupShortLong(TIFFTAG.TIFFTAG_IMAGEWIDTH, ref data[dir++], m_dir.td_imagewidth);
                        setupShortLong(TIFFTAG.TIFFTAG_IMAGELENGTH, ref data[dir], m_dir.td_imagelength);
                        break;
                    case FIELD.FIELD_TILEDIMENSIONS:
                        setupShortLong(TIFFTAG.TIFFTAG_TILEWIDTH, ref data[dir++], m_dir.td_tilewidth);
                        setupShortLong(TIFFTAG.TIFFTAG_TILELENGTH, ref data[dir], m_dir.td_tilelength);
                        break;
                    case FIELD.FIELD_COMPRESSION:
                        setupShort((uint)TIFFTAG.TIFFTAG_COMPRESSION, ref data[dir], (ushort)m_dir.td_compression);
                        break;
                    case FIELD.FIELD_PHOTOMETRIC:
                        setupShort((uint)TIFFTAG.TIFFTAG_PHOTOMETRIC, ref data[dir], (ushort)m_dir.td_photometric);
                        break;
                    case FIELD.FIELD_POSITION:
                        if (!writeRationalPair(data, dir, TiffDataType.TIFF_RATIONAL, TIFFTAG.TIFFTAG_XPOSITION, m_dir.td_xposition, TIFFTAG.TIFFTAG_YPOSITION, m_dir.td_yposition))
                            return false;

                        dir++;
                        break;
                    case FIELD.FIELD_RESOLUTION:
                        if (!writeRationalPair(data, dir, TiffDataType.TIFF_RATIONAL, TIFFTAG.TIFFTAG_XRESOLUTION, m_dir.td_xresolution, TIFFTAG.TIFFTAG_YRESOLUTION, m_dir.td_yresolution))
                            return false;

                        dir++;
                        break;
                    case FIELD.FIELD_BITSPERSAMPLE:
                    case FIELD.FIELD_MINSAMPLEVALUE:
                    case FIELD.FIELD_MAXSAMPLEVALUE:
                    case FIELD.FIELD_SAMPLEFORMAT:
                        if (!writePerSampleShorts(fip.field_tag, ref data[dir]))
                            return false;

                        break;
                    case FIELD.FIELD_SMINSAMPLEVALUE:
                    case FIELD.FIELD_SMAXSAMPLEVALUE:
                        if (!writePerSampleAnys(sampleToTagType(), fip.field_tag, ref data[dir]))
                            return false;

                        break;
                    case FIELD.FIELD_PAGENUMBER:
                    case FIELD.FIELD_HALFTONEHINTS:
                    case FIELD.FIELD_YCBCRSUBSAMPLING:
                        if (!setupShortPair(fip.field_tag, ref data[dir]))
                            return false;

                        break;
                    case FIELD.FIELD_INKNAMES:
                        if (!writeInkNames(ref data[dir]))
                            return false;

                        break;
                    case FIELD.FIELD_TRANSFERFUNCTION:
                        if (!writeTransferFunction(ref data[dir]))
                            return false;

                        break;
                    case FIELD.FIELD_SUBIFD:
                        /*
                         * XXX: Always write this field using LONG type
                         * for backward compatibility.
                         */
                        data[dir].tdir_tag = fip.field_tag;
                        data[dir].tdir_type = TiffDataType.TIFF_LONG;
                        data[dir].tdir_count = m_dir.td_nsubifd;
                        if (!writeLongArray(ref data[dir], m_dir.td_subifd))
                            return false;

                        /*
                         * Total hack: if this directory includes a SubIFD
                         * tag then force the next <n> directories to be
                         * written as ``sub directories'' of this one.  This
                         * is used to write things like thumbnails and
                         * image masks that one wants to keep out of the
                         * normal directory linkage access mechanism.
                         */
                        if (data[dir].tdir_count > 0)
                        {
                            m_flags |= TIFF_INSUBIFD;
                            m_nsubifd = (UInt16)data[dir].tdir_count;
                            if (data[dir].tdir_count > 1)
                                m_subifdoff = data[dir].tdir_offset;
                            else
                            {
                                Debug.Assert(false);
                                m_subifdoff = m_diroff + sizeof(UInt16) + dir * TiffDirEntry.SizeInBytes + sizeof(UInt16) * 2 + sizeof(uint);
                            }
                        }
                        break;
                    default:
                        /* XXX: Should be fixed and removed. */
                        if (fip.field_tag == TIFFTAG.TIFFTAG_DOTRANGE)
                        {
                            if (!setupShortPair(fip.field_tag, ref data[dir]))
                                return false;
                        }
                        else if (!writeNormalTag(ref data[dir], fip))
                            return false;

                        break;
                }

                dir++;

                if (fip.field_bit != FIELD.FIELD_CUSTOM)
                    resetFieldBit(fields, fip.field_bit);
            }

            /*
             * Write directory.
             */
            UInt16 dircount = (UInt16)nfields;
            int diroff = m_nextdiroff;
            if ((m_flags & TIFF_SWAB) != 0)
            {
                /*
                 * The file's byte order is opposite to the
                 * native machine architecture.  We overwrite
                 * the directory information with impunity
                 * because it'll be released below after we
                 * write it to the file.  Note that all the
                 * other tag construction routines assume that
                 * we do this byte-swapping; i.e. they only
                 * byte-swap indirect data.
                 */
                for (dir = 0; dircount != 0; dir++, dircount--)
                {
                    ushort temp = data[dir].tdir_tag;
                    SwabShort(ref temp);
                    data[dir].tdir_tag = temp;

                    temp = data[dir].tdir_type;
                    SwabShort(ref temp);
                    data[dir].tdir_type = temp;

                    SwabLong(ref data[dir].tdir_count);
                    SwabLong(ref data[dir].tdir_offset);
                }

                dircount = (UInt16)nfields;
                SwabShort(ref dircount);
                SwabLong(ref diroff);
            }
            
            seekFile(m_diroff, SEEK_SET);
            if (!writeUInt16OK(dircount))
            {
                ErrorExt(this, m_clientdata, m_name, "Error writing directory count");
                return false;
            }
            
            if (!writeDirEntryOK(data, dirsize / TiffDirEntry.SizeInBytes))
            {
                ErrorExt(this, m_clientdata, m_name, "Error writing directory contents");
                return false;
            }
            
            if (!writeIntOK(diroff))
            {
                ErrorExt(this, m_clientdata, m_name, "Error writing directory link");
                return false;
            }
            
            if (done)
            {
                FreeDirectory();
                m_flags &= ~TIFF_DIRTYDIRECT;
                m_currentCodec.tif_cleanup();

                /*
                 * Reset directory-related state for subsequent
                 * directories.
                 */
                CreateDirectory();
            }

            return true;
        }

        /*
        * Process tags that are not special cased.
        */
        private bool writeNormalTag(ref TiffDirEntry dir, TiffFieldInfo fip)
        {
            UInt16 wc = (UInt16)fip.field_writecount;
            dir.tdir_tag = (UInt16)fip.field_tag;
            dir.tdir_type = (UInt16)fip.field_type;
            dir.tdir_count = wc;

            switch (fip.field_type)
            {
                case TiffDataType.TIFF_SHORT:
                case TiffDataType.TIFF_SSHORT:
                    if (fip.field_passcount)
                    {
                        UInt16[] wp;
                        uint wc2;
                        if (wc == (UInt16)TIFF_VARIABLE2)
                        {
                            GetField(fip.field_tag, &wc2, &wp);
                            dir.tdir_count = wc2;
                        }
                        else
                        {
                            /* Assume TIFF_VARIABLE */
                            GetField(fip.field_tag, &wc, &wp);
                            dir.tdir_count = wc;
                        }

                        if (!writeShortArray(dir, wp))
                            return false;
                    }
                    else
                    {
                        if (wc == 1)
                        {
                            UInt16 sv;
                            GetField(fip.field_tag, &sv);
                            dir.tdir_offset = insertData(dir.tdir_type, sv);
                        }
                        else
                        {
                            UInt16[] wp;
                            GetField(fip.field_tag, &wp);
                            if (!writeShortArray(dir, wp))
                                return false;
                        }
                    }
                    break;
                case TiffDataType.TIFF_LONG:
                case TiffDataType.TIFF_SLONG:
                case TiffDataType.TIFF_IFD:
                    if (fip.field_passcount)
                    {
                        uint[] lp;
                        uint wc2;
                        if (wc == (UInt16)TIFF_VARIABLE2)
                        {
                            GetField(fip.field_tag, &wc2, &lp);
                            dir.tdir_count = wc2;
                        }
                        else
                        {
                            /* Assume TIFF_VARIABLE */
                            GetField(fip.field_tag, &wc, &lp);
                            dir.tdir_count = wc;
                        }

                        if (!writeLongArray(dir, lp))
                            return false;
                    }
                    else
                    {
                        if (wc == 1)
                        {
                            /* XXX handle LONG.SHORT conversion */
                            GetField(fip.field_tag, &dir.tdir_offset);
                        }
                        else
                        {
                            uint[] lp;
                            GetField(fip.field_tag, &lp);
                            if (!writeLongArray(dir, lp))
                                return false;
                        }
                    }
                    break;
                case TiffDataType.TIFF_RATIONAL:
                case TiffDataType.TIFF_SRATIONAL:
                    if (fip.field_passcount)
                    {
                        float[] fp;
                        uint wc2;
                        if (wc == (UInt16)TIFF_VARIABLE2)
                        {
                            GetField(fip.field_tag, &wc2, &fp);
                            dir.tdir_count = wc2;
                        }
                        else
                        {
                            /* Assume TIFF_VARIABLE */
                            GetField(fip.field_tag, &wc, &fp);
                            dir.tdir_count = wc;
                        }

                        if (!writeRationalArray(dir, fp))
                            return false;
                    }
                    else
                    {
                        if (wc == 1)
                        {
                            float[] fv = new float[1];
                            GetField(fip.field_tag, &fv[0]);
                            if (!writeRationalArray(dir, fv))
                                return false;
                        }
                        else
                        {
                            float[] fp;
                            GetField(fip.field_tag, &fp);
                            if (!writeRationalArray(dir, fp))
                                return false;
                        }
                    }
                    break;
                case TiffDataType.TIFF_FLOAT:
                    if (fip.field_passcount)
                    {
                        float[] fp;
                        uint wc2;
                        if (wc == (UInt16)TIFF_VARIABLE2)
                        {
                            GetField(fip.field_tag, &wc2, &fp);
                            dir.tdir_count = wc2;
                        }
                        else
                        {
                            /* Assume TIFF_VARIABLE */
                            GetField(fip.field_tag, &wc, &fp);
                            dir.tdir_count = wc;
                        }

                        if (!writeFloatArray(dir, fp))
                            return false;
                    }
                    else
                    {
                        if (wc == 1)
                        {
                            float[] fv = new float[1];
                            GetField(fip.field_tag, &fv[0]);
                            if (!writeFloatArray(dir, fv))
                                return false;
                        }
                        else
                        {
                            float[] fp;
                            GetField(fip.field_tag, &fp);
                            if (!writeFloatArray(dir, fp))
                                return false;
                        }
                    }
                    break;
                case TiffDataType.TIFF_DOUBLE:
                    if (fip.field_passcount)
                    {
                        double[] dp;
                        uint wc2;
                        if (wc == (UInt16)TIFF_VARIABLE2)
                        {
                            GetField(fip.field_tag, &wc2, &dp);
                            dir.tdir_count = wc2;
                        }
                        else
                        {
                            /* Assume TIFF_VARIABLE */
                            GetField(fip.field_tag, &wc, &dp);
                            dir.tdir_count = wc;
                        }

                        if (!writeDoubleArray(dir, dp))
                            return false;
                    }
                    else
                    {
                        if (wc == 1)
                        {
                            double[] dv = new double[1];
                            GetField(fip.field_tag, &dv[0]);
                            if (!writeDoubleArray(dir, dv))
                                return false;
                        }
                        else
                        {
                            double[] dp;
                            GetField(fip.field_tag, &dp);
                            if (!writeDoubleArray(dir, dp))
                                return false;
                        }
                    }
                    break;
                case TiffDataType.TIFF_ASCII:
                    {
                        char* cp;
                        uint wc2;
                        if (fip.field_passcount)
                        {
                            if (wc == (UInt16)TIFF_VARIABLE2)
                                GetField(fip.field_tag, &wc2, &cp);
                            else
                                GetField(fip.field_tag, &wc, &cp);
                        }
                        else
                            GetField(fip.field_tag, &cp);

                        dir.tdir_count = (uint)(strlen(cp) + 1);
                        if (!writeByteArray(dir, cp))
                            return false;
                    }
                    break;

                case TiffDataType.TIFF_BYTE:
                case TiffDataType.TIFF_SBYTE:
                    if (fip.field_passcount)
                    {
                        char* cp;
                        uint wc2;
                        if (wc == (UInt16)TIFF_VARIABLE2)
                        {
                            GetField(fip.field_tag, &wc2, &cp);
                            dir.tdir_count = wc2;
                        }
                        else
                        {
                            /* Assume TIFF_VARIABLE */
                            GetField(fip.field_tag, &wc, &cp);
                            dir.tdir_count = wc;
                        }

                        if (!writeByteArray(dir, cp))
                            return false;
                    }
                    else
                    {
                        if (wc == 1)
                        {
                            char cv[1];
                            GetField(fip.field_tag, &cv[0]);
                            if (!writeByteArray(dir, cv))
                                return false;
                        }
                        else
                        {
                            char* cp;
                            GetField(fip.field_tag, &cp);
                            if (!writeByteArray(dir, cp))
                                return false;
                        }
                    }
                    break;

                case TiffDataType.TIFF_UNDEFINED:
                    {
                        char* cp;
                        uint wc2;
                        if (wc == (ushort)TIFF_VARIABLE)
                        {
                            GetField(fip.field_tag, &wc, &cp);
                            dir.tdir_count = wc;
                        }
                        else if (wc == (ushort)TIFF_VARIABLE2)
                        {
                            GetField(fip.field_tag, &wc2, &cp);
                            dir.tdir_count = wc2;
                        }
                        else
                            GetField(fip.field_tag, &cp);

                        if (!writeByteArray(dir, cp))
                            return false;
                    }
                    break;

                case TIFF_NOTYPE:
                    break;
            }

            return true;
        }

        /*
        * Setup a directory entry with either a SHORT
        * or LONG type according to the value.
        */
        private void setupShortLong(TIFFTAG tag, ref TiffDirEntry dir, int v)
        {
            dir.tdir_tag = tag;
            dir.tdir_count = 1;
            if (v > 0xffffL)
            {
                dir.tdir_type = TiffDataType.TIFF_LONG;
                dir.tdir_offset = v;
            }
            else
            {
                dir.tdir_type = TiffDataType.TIFF_SHORT;
                dir.tdir_offset = insertData(TiffDataType.TIFF_SHORT, v);
            }
        }

        /*
        * Setup a SHORT directory entry
        */
        private void setupShort(uint tag, ref TiffDirEntry dir, UInt16 v)
        {
            dir.tdir_tag = tag;
            dir.tdir_count = 1;
            dir.tdir_type = TiffDataType.TIFF_SHORT;
            dir.tdir_offset = insertData(TiffDataType.TIFF_SHORT, v);
        }

        /*
        * Setup a directory entry that references a
        * samples/pixel array of SHORT values and
        * (potentially) write the associated indirect
        * values.
        */
        private bool writePerSampleShorts(TIFFTAG tag, ref TiffDirEntry dir)
        {
            UInt16[] w = new UInt16 [m_dir.td_samplesperpixel];
            if (w == null)
            {
                ErrorExt(this, m_clientdata, m_name, "No space to write per-sample shorts");
                return false;
            }

            UInt16 v;
            GetField(tag, &v);
            for (UInt16 i = 0; i < m_dir.td_samplesperpixel; i++)
                w[i] = v;

            dir.tdir_tag = (UInt16)tag;
            dir.tdir_type = TiffDataType.TIFF_SHORT;
            dir.tdir_count = m_dir.td_samplesperpixel;
            bool status = writeShortArray(ref dir, w);
            return status;
        }

        /*
        * Setup a directory entry that references a samples/pixel array of ``type''
        * values and (potentially) write the associated indirect values.  The source
        * data from GetField() for the specified tag must be returned as double.
        */
        private bool writePerSampleAnys(TiffDataType type, TIFFTAG tag, ref TiffDirEntry dir)
        {
            double[] w = new double [m_dir.td_samplesperpixel];
            if (w == null)
            {
                ErrorExt(this, m_clientdata, m_name, "No space to write per-sample values");
                return false;
            }

            double v;
            GetField(tag, &v);
            for (UInt16 i = 0; i < m_dir.td_samplesperpixel; i++)
                w[i] = v;
            
            bool status = writeAnyArray(type, tag, ref dir, m_dir.td_samplesperpixel, w);
            return status;
        }

        /*
        * Setup a pair of shorts that are returned by
        * value, rather than as a reference to an array.
        */
        private bool setupShortPair(TIFFTAG tag, ref TiffDirEntry dir)
        {
            UInt16[] v = new ushort[2];
            GetField(tag, &v[0], &v[1]);

            dir.tdir_tag = (UInt16)tag;
            dir.tdir_type = TiffDataType.TIFF_SHORT;
            dir.tdir_count = 2;
            return writeShortArray(dir, v);
        }

        /*
        * Setup a directory entry for an NxM table of shorts,
        * where M is known to be 2**bitspersample, and write
        * the associated indirect data.
        */
        private bool writeShortTable(TIFFTAG tag, ref TiffDirEntry dir, int n, UInt16[][] table)
        {
            dir.tdir_tag = tag;
            dir.tdir_type = TiffDataType.TIFF_SHORT;
            /* XXX -- yech, fool writeData */
            dir.tdir_count = 1L << m_dir.td_bitspersample;
            uint off = m_dataoff;
            for (uint i = 0; i < n; i++)
            {
                if (!writeData(ref dir, table[i], dir.tdir_count))
                    return false;
            }

            dir.tdir_count *= n;
            dir.tdir_offset = off;
            return true;
        }

        /*
        * Write/copy data associated with an ASCII or opaque tag value.
        */
        private bool writeByteArray(ref TiffDirEntry dir, byte[] cp)
        {
            if (dir.tdir_count <= 4)
            {
                if (m_header.tiff_magic == TIFF_BIGENDIAN)
                {
                    dir.tdir_offset = (uint)cp[0] << 24;
                    if (dir.tdir_count >= 2)
                        dir.tdir_offset |= (uint)cp[1] << 16;

                    if (dir.tdir_count >= 3)
                        dir.tdir_offset |= (uint)cp[2] << 8;

                    if (dir.tdir_count == 4)
                        dir.tdir_offset |= cp[3];
                }
                else
                {
                    dir.tdir_offset = cp[0];
                    if (dir.tdir_count >= 2)
                        dir.tdir_offset |= (uint)cp[1] << 8;

                    if (dir.tdir_count >= 3)
                        dir.tdir_offset |= (uint)cp[2] << 16;

                    if (dir.tdir_count == 4)
                        dir.tdir_offset |= (uint)cp[3] << 24;
                }

                return true;
            }

            return writeData(dir, cp, dir.tdir_count);
        }

        /*
        * Setup a directory entry of an array of SHORT
        * or SSHORT and write the associated indirect values.
        */
        private bool writeShortArray(ref TiffDirEntry dir, UInt16[] v)
        {
            if (dir.tdir_count <= 2)
            {
                if (m_header.tiff_magic == TIFF_BIGENDIAN)
                {
                    dir.tdir_offset = (uint)v[0] << 16;
                    if (dir.tdir_count == 2)
                        dir.tdir_offset |= v[1] & 0xffff;
                }
                else
                {
                    dir.tdir_offset = v[0] & 0xffff;
                    if (dir.tdir_count == 2)
                        dir.tdir_offset |= (uint)v[1] << 16;
                }

                return true;
            }

            return writeData(dir, v, dir.tdir_count);
        }

        /*
        * Setup a directory entry of an array of LONG
        * or SLONG and write the associated indirect values.
        */
        private bool writeLongArray(ref TiffDirEntry dir, int[] v)
        {
            if (dir.tdir_count == 1)
            {
                dir.tdir_offset = v[0];
                return true;
            }

            return writeData(dir, v, dir.tdir_count);
        }

        /*
        * Setup a directory entry of an array of RATIONAL
        * or SRATIONAL and write the associated indirect values.
        */
        private bool writeRationalArray(ref TiffDirEntry dir, float[] v)
        {
            uint[] t = new uint [2 * dir.tdir_count];
            if (t == null)
            {
                ErrorExt(this, m_clientdata, m_name, "No space to write RATIONAL array");
                return false;
            }

            for (uint i = 0; i < dir.tdir_count; i++)
            {
                int sign = 1;
                float fv = v[i];
                if (fv < 0)
                {
                    if (dir.tdir_type == TiffDataType.TIFF_RATIONAL)
                    {
                        WarningExt(this, m_clientdata, m_name, "\"%s\": Information lost writing value (%g) as (unsigned) RATIONAL", FieldWithTag(dir.tdir_tag).field_name, fv);
                        fv = 0;
                    }
                    else
                    {
                        fv = -fv;
                        sign = -1;
                    }
                }
            
                uint den = 1L;
                
                if (fv > 0)
                {
                    while (fv < (1L << (31 - 3)) && den < (1L << (31 - 3)))
                    {
                        fv *= 1 << 3;
                        den *= 1L << 3;
                    }
                }
                
                t[2 * i + 0] = (uint)(sign * (fv + 0.5));
                t[2 * i + 1] = den;
            }

            bool status = writeData(dir, t, 2 * dir.tdir_count);
            return status;
        }

        private bool writeFloatArray(ref TiffDirEntry dir, float[] v)
        {
            if (dir.tdir_count == 1)
            {
                dir.tdir_offset = *(uint*)&v[0];
                return true;
            }

            return writeData(dir, v, dir.tdir_count);
        }

        private bool writeDoubleArray(ref TiffDirEntry dir, double[] v)
        {
            return writeData(dir, v, dir.tdir_count);
        }

        /*
        * Write an array of ``type'' values for a specified tag (i.e. this is a tag
        * which is allowed to have different types, e.g. SMaxSampleType).
        * Internally the data values are represented as double since a double can
        * hold any of the TIFF tag types (yes, this should really be an abstract
        * type tany_t for portability).  The data is converted into the specified
        * type in a temporary buffer and then handed off to the appropriate array
        * writer.
        */
        private bool writeAnyArray(TiffDataType type, uint tag, ref TiffDirEntry dir, int n, double[] v)
        {
            dir.tdir_tag = (UInt16)tag;
            dir.tdir_type = (UInt16)type;
            dir.tdir_count = n;

            bool failed = false;
            switch (type)
            {
                case TiffDataType.TIFF_BYTE:
                    {
                        byte[] bp = new byte [n];
                        if (bp == null)
                        {
                            ErrorExt(this, m_clientdata, m_name, "No space to write array");
                            return false;
                        }

                        for (int i = 0; i < n; i++)
                            bp[i] = (byte)v[i];

                        if (!writeByteArray(dir, bp))
                            failed = true;
                    }
                    break;
                case TiffDataType.TIFF_SBYTE:
                    {
                        sbyte[] bp = new sbyte [n];
                        if (bp == null)
                        {
                            ErrorExt(this, m_clientdata, m_name, "No space to write array");
                            return false;
                        }
                        
                        for (int i = 0; i < n; i++)
                            bp[i] = (sbyte)v[i];
                        
                        if (!writeByteArray(dir, bp))
                            failed = true;
                    }
                    break;
                case TiffDataType.TIFF_SHORT:
                    {
                        UInt16[] bp = new UInt16 [n];
                        if (bp == null)
                        {
                            ErrorExt(this, m_clientdata, m_name, "No space to write array");
                            return false;
                        }
                        
                        for (int i = 0; i < n; i++)
                            bp[i] = (UInt16)v[i];
                        
                        if (!writeShortArray(dir, bp))
                            failed = true;
                    }
                    break;
                case TiffDataType.TIFF_SSHORT:
                    {
                        Int16[] bp = new Int16 [n];
                        if (bp == null)
                        {
                            ErrorExt(this, m_clientdata, m_name, "No space to write array");
                            return false;
                        }
                        
                        for (int i = 0; i < n; i++)
                            bp[i] = (Int16)v[i];
                        
                        if (!writeShortArray(dir, (UInt16*)bp))
                            failed = true;
                    }
                    break;
                case TiffDataType.TIFF_LONG:
                    {
                        uint[] bp = new uint [n];
                        if (bp == null)
                        {
                            ErrorExt(this, m_clientdata, m_name, "No space to write array");
                            return false;
                        }
                        
                        for (int i = 0; i < n; i++)
                            bp[i] = (uint)v[i];
                        
                        if (!writeLongArray(dir, bp))
                            failed = true;
                    }
                    break;
                case TiffDataType.TIFF_SLONG:
                    {
                        int* bp = new int [n];
                        if (bp == null)
                        {
                            ErrorExt(this, m_clientdata, m_name, "No space to write array");
                            return false;
                        }
                        
                        for (int i = 0; i < n; i++)
                            bp[i] = (int)v[i];
                        
                        if (!writeLongArray(dir, (uint*)bp))
                            failed = true;
                    }
                    break;
                case TiffDataType.TIFF_FLOAT:
                    {
                        float[] bp = new float [n];
                        if (bp == null)
                        {
                            ErrorExt(this, m_clientdata, m_name, "No space to write array");
                            return false;
                        }
                        
                        for (int i = 0; i < n; i++)
                            bp[i] = (float)v[i];
                        
                        if (!writeFloatArray(dir, bp))
                            failed = true;
                    }
                    break;
                case TiffDataType.TIFF_DOUBLE:
                    if (!writeDoubleArray(dir, v))
                        failed = true;
                        
                    break;

                default:
                    /* TIFF_NOTYPE */
                    /* TIFF_ASCII */
                    /* TIFF_UNDEFINED */
                    /* TIFF_RATIONAL */
                    /* TIFF_SRATIONAL */
                    failed = true;
                    break;
            }

            return !failed;
        }

        private bool writeTransferFunction(ref TiffDirEntry dir)
        {
            /*
             * Check if the table can be written as a single column,
             * or if it must be written as 3 columns.  Note that we
             * write a 3-column tag if there are 2 samples/pixel and
             * a single column of data won't suffice--hmm.
             */
            UInt16 u = m_dir.td_samplesperpixel - m_dir.td_extrasamples;
            int ncols = 1;
            bool reCheck = false;
            int n = (1L << m_dir.td_bitspersample);

            if (u < 0 || u > 2)
            {
                if (Compare(m_dir.td_transferfunction[0], m_dir.td_transferfunction[2], n))
                    ncols = 3;
                else
                    reCheck = true;
            }

            if (u == 2 || reCheck)
            {
                if (Compare(m_dir.td_transferfunction[0], m_dir.td_transferfunction[1], n))
                    ncols = 3;
            }
            
            return writeShortTable(TIFFTAG.TIFFTAG_TRANSFERFUNCTION, dir, ncols, m_dir.td_transferfunction);
        }

        private bool writeInkNames(ref TiffDirEntry dir)
        {
            dir.tdir_tag = TIFFTAG.TIFFTAG_INKNAMES;
            dir.tdir_type = TiffDataType.TIFF_ASCII;
            dir.tdir_count = m_dir.td_inknameslen;
            return writeByteArray(dir, m_dir.td_inknames);
        }

        /*
        * Write a contiguous directory item.
        */
        private bool writeData(ref TiffDirEntry dir, byte[] cp, int cc)
        {
            dir.tdir_offset = m_dataoff;
            cc = dir.tdir_count * DataWidth(dir.tdir_type);
            if (seekOK(dir.tdir_offset) && writeOK(cp, cc))
            {
                m_dataoff += (cc + 1) & ~1;
                return true;
            }

            ErrorExt(this, m_clientdata, m_name, "Error writing data for field \"%s\"", FieldWithTag(dir.tdir_tag).field_name);
            return false;
        }

        private bool writeData(ref TiffDirEntry dir, UInt16[] cp, uint cc)
        {
            if ((m_flags & TIFF_SWAB) != 0)
                SwabArrayOfShort(cp, cc);

            int byteCount = cc * sizeof(UInt16);
            byte[] bytes = new byte [byteCount];
            uint16ToByteArray(cp, 0, cc, bytes, 0);
            bool res = writeData(dir, bytes, byteCount);
            return res;
        }

        private bool writeData(ref TiffDirEntry dir, uint[] cp, uint cc)
        {
            if ((m_flags & TIFF_SWAB) != 0)
                SwabArrayOfLong(cp, cc);

            int byteCount = cc * sizeof(uint);
            byte[] bytes = new byte [byteCount];
            uintToByteArray(cp, 0, cc, bytes, 0);
            bool res = writeData(dir, bytes, byteCount);
            return res;
        }

        private bool writeData(ref TiffDirEntry dir, float[] cp, uint cc)
        {
            // hack
            return writeData(dir, (uint*)cp, cc);
        }

        private bool writeData(ref TiffDirEntry dir, double[] cp, uint cc)
        {
            if ((m_flags & TIFF_SWAB) != 0)
                SwabArrayOfDouble(cp, cc);

            int byteCount = cc * sizeof(double);
            byte[] bytes = new byte [byteCount];
            memcpy(bytes, cp, byteCount);
            bool res = writeData(dir, bytes, byteCount);
            return res;
        }

        /*
        * Link the current directory into the
        * directory chain for the file.
        */
        private bool linkDirectory()
        {
            const string module = "linkDirectory";

            m_diroff = (seekFile(0, SEEK_END) + 1) & ~1;
            uint diroff = m_diroff;
            if ((m_flags & TIFF_SWAB) != 0)
                SwabLong(ref diroff);

            /*
             * Handle SubIFDs
             */
            if ((m_flags & TIFF_INSUBIFD) != 0)
            {
                seekFile(m_subifdoff, SEEK_SET);
                if (!writeIntOK(diroff))
                {
                    ErrorExt(this, m_clientdata, module, "%s: Error writing SubIFD directory link", m_name);
                    return false;
                }

                /*
                 * Advance to the next SubIFD or, if this is
                 * the last one configured, revert back to the
                 * normal directory linkage.
                 */
                --m_nsubifd;

                if (m_nsubifd != 0)
                    m_subifdoff += sizeof(uint);
                else
                    m_flags &= ~TIFF_INSUBIFD;
                
                return true;
            }

            if (m_header.tiff_diroff == 0)
            {
                /*
                 * First directory, overwrite offset in header.
                 */
                m_header.tiff_diroff = m_diroff;
                seekFile((uint)(TiffHeader.TIFF_MAGIC_SIZE + TiffHeader.TIFF_VERSION_SIZE), SEEK_SET);
                if (!writeIntOK(diroff))
                {
                    ErrorExt(this, m_clientdata, m_name, "Error writing TIFF header");
                    return false;
                }

                return true;
            }

            /*
             * Not the first directory, search to the last and append.
             */
            uint nextdir = m_header.tiff_diroff;
            do
            {
                UInt16 dircount;
                if (!seekOK(nextdir) || !readUInt16OK(out dircount))
                {
                    ErrorExt(this, m_clientdata, module, "Error fetching directory count");
                    return false;
                }
                
                if ((m_flags & TIFF_SWAB) != 0)
                    SwabShort(ref dircount);
                
                seekFile(dircount * TiffDirEntry.SizeInBytes, SEEK_CUR);
                if (!readIntOK(out nextdir))
                {
                    ErrorExt(this, m_clientdata, module, "Error fetching directory link");
                    return false;
                }

                if ((m_flags & TIFF_SWAB) != 0)
                    SwabLong(ref nextdir);
            }
            while (nextdir != 0);

            uint off = seekFile(0, SEEK_CUR); /* get current offset */
            seekFile(off - (uint)sizeof(uint), SEEK_SET);
            if (!writeIntOK(diroff))
            {
                ErrorExt(this, m_clientdata, module, "Error writing directory link");
                return false;
            }

            return true;
        }
    }
}
