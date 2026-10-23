#!/usr/bin/env bash
# Prints the constant-buffer layout (name, byte offset, register) of one pass of a shader exported by AssetRipper in
# YAML mode, from m_NameIndices and the programs' m_CommonParameters. Complements tools/ShaderDis.cs, whose .asm files
# only name what the per-program parameter blobs carry.
# Usage: tools/shader-cbuffers.sh <shader .asset> <pass number, 1-based>
asset="$1"; want="${2:-1}"
awk -v want="$want" '
/m_NameIndices:/ {pass++; inmap=1; next}
inmap && /^          [^ ].*: [0-9]+\s*$/ {split($0,a,": "); gsub(/^ +/,"",a[1]); name[pass","a[2]+0]=a[1]; next}
inmap {inmap=0}
/m_NameIndex:/ {ni=$NF+0}
/ m_Index:/ && pass==want {print name[pass","ni] " " $NF " (c" int($NF/16) "." substr("xyzw", ($NF%16)/4+1, 1) ")"}
/m_TextureParams:/ && pass==want {tex=1}
/m_SamplerIndex:/ && pass==want {print "  sampler " $NF}
' "$asset" | awk '!seen[$0]++'
