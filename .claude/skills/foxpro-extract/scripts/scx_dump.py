"""Dump FoxPro .scx form: OBJNAME, CLASS, PARENT, PROPERTIES, METHODS."""
import struct, sys, io

scx_path = sys.argv[1]
sct_path = sys.argv[2]
out_path = sys.argv[3]

with open(scx_path, 'rb') as f:
    dbf = f.read()
with open(sct_path, 'rb') as f:
    sct = f.read()

nrecs = struct.unpack('<I', dbf[4:8])[0]
hdr_len = struct.unpack('<H', dbf[8:10])[0]
rec_len = struct.unpack('<H', dbf[10:12])[0]

# parse field descriptors
fields = []
pos = 32
while dbf[pos] != 0x0D:
    name = dbf[pos:pos+11].split(b'\x00')[0].decode('ascii')
    ftype = chr(dbf[pos+11])
    flen = dbf[pos+16]
    fields.append((name, ftype, flen))
    pos += 32

def memo_text(ptr):
    if ptr <= 0:
        return ''
    # block size = 1 in .sct → ptr is byte offset
    if ptr + 8 > len(sct):
        return ''
    btype = struct.unpack('>I', sct[ptr:ptr+4])[0]
    blen = struct.unpack('>I', sct[ptr+4:ptr+8])[0]
    raw = sct[ptr+8:ptr+8+blen]
    return raw.decode('cp1252', errors='replace')

out = io.StringIO()
for i in range(nrecs):
    rpos = hdr_len + i * rec_len
    rec = dbf[rpos:rpos+rec_len]
    if not rec or rec[0:1] == b'*':
        continue
    fpos = 1
    vals = {}
    for (name, ftype, flen) in fields:
        raw = rec[fpos:fpos+flen]
        fpos += flen
        if ftype == 'M':
            ptr = struct.unpack('<i', raw)[0] if flen == 4 else 0
            vals[name] = memo_text(ptr)
        else:
            vals[name] = raw.decode('cp1252', errors='replace').strip()
    objname = vals.get('OBJNAME', '')
    klass = vals.get('CLASS', '')
    parent = vals.get('PARENT', '')
    props = vals.get('PROPERTIES', '')
    methods = vals.get('METHODS', '')
    if not (objname or props or methods):
        continue
    out.write(f"\n{'='*90}\n### REC {i} | OBJNAME: {objname} | CLASS: {klass} | PARENT: {parent}\n")
    if props.strip():
        out.write(f"--- PROPERTIES ---\n{props}\n")
    if methods.strip():
        out.write(f"--- METHODS ---\n{methods}\n")

with open(out_path, 'w', encoding='utf-8') as f:
    f.write(out.getvalue())
print(f"OK: {nrecs} registros, {len(fields)} campos -> {out_path}")
