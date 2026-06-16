/* Minimal QR code encoder — no dependencies (Teil B §2: no CDN/runtime deps).
 * Scope: byte mode, ECC level M, versions 1–10 (≈ up to 213 bytes), fixed mask 0.
 * Algorithm follows the structure of Nayuki's public-domain "QR Code generator".
 * Exposed as window.qrEncode(text) → boolean[size][size] (true = dark module),
 * or module.exports for the Node-based round-trip test. */
(function (global) {
  "use strict";

  // ECC level M tables, index = version (1-based)
  var ECC_PER_BLOCK = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
  var NUM_BLOCKS = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];
  var MAX_VERSION = 10;

  function getNumRawDataModules(ver) {
    var result = (16 * ver + 128) * ver + 64;
    if (ver >= 2) {
      var numAlign = Math.floor(ver / 7) + 2;
      result -= (25 * numAlign - 10) * numAlign - 55;
      if (ver >= 7) result -= 36;
    }
    return result;
  }

  function getNumDataCodewords(ver) {
    return Math.floor(getNumRawDataModules(ver) / 8) - ECC_PER_BLOCK[ver] * NUM_BLOCKS[ver];
  }

  // ── Reed-Solomon over GF(2^8), reducing polynomial 0x11D ──────────────────
  function rsMultiply(x, y) {
    var z = 0;
    for (var i = 7; i >= 0; i--) {
      z = (z << 1) ^ ((z >>> 7) * 0x11d);
      z ^= ((y >>> i) & 1) * x;
    }
    return z & 0xff;
  }

  function rsComputeDivisor(degree) {
    var result = [];
    for (var i = 0; i < degree - 1; i++) result.push(0);
    result.push(1); // x^0 coefficient last
    var root = 1;
    for (var d = 0; d < degree; d++) {
      for (var j = 0; j < result.length; j++) {
        result[j] = rsMultiply(result[j], root);
        if (j + 1 < result.length) result[j] ^= result[j + 1];
      }
      root = rsMultiply(root, 0x02);
    }
    return result;
  }

  function rsComputeRemainder(data, divisor) {
    var result = divisor.map(function () { return 0; });
    for (var i = 0; i < data.length; i++) {
      var factor = data[i] ^ result.shift();
      result.push(0);
      for (var j = 0; j < divisor.length; j++)
        result[j] ^= rsMultiply(divisor[j], factor);
    }
    return result;
  }

  // ── encoding ───────────────────────────────────────────────────────────────
  function toUtf8Bytes(text) {
    var bytes = [];
    var encoded = unescape(encodeURIComponent(text));
    for (var i = 0; i < encoded.length; i++) bytes.push(encoded.charCodeAt(i));
    return bytes;
  }

  function pickVersion(byteLen) {
    for (var ver = 1; ver <= MAX_VERSION; ver++) {
      var ccBits = ver <= 9 ? 8 : 16; // byte-mode char count width
      var usedBits = 4 + ccBits + byteLen * 8;
      if (usedBits <= getNumDataCodewords(ver) * 8) return ver;
    }
    return -1;
  }

  function buildCodewords(bytes, ver) {
    var bits = [];
    function appendBits(val, len) {
      for (var i = len - 1; i >= 0; i--) bits.push((val >>> i) & 1);
    }

    appendBits(4, 4); // byte mode
    appendBits(bytes.length, ver <= 9 ? 8 : 16);
    for (var i = 0; i < bytes.length; i++) appendBits(bytes[i], 8);

    var capacityBits = getNumDataCodewords(ver) * 8;
    appendBits(0, Math.min(4, capacityBits - bits.length)); // terminator
    appendBits(0, (8 - bits.length % 8) % 8);               // byte align
    for (var pad = 0xec; bits.length < capacityBits; pad ^= 0xec ^ 0x11)
      appendBits(pad, 8);

    var data = [];
    for (var b = 0; b < bits.length; b += 8) {
      var v = 0;
      for (var j = 0; j < 8; j++) v = (v << 1) | bits[b + j];
      data.push(v);
    }
    return data;
  }

  /** Splits data into blocks, appends ECC, interleaves (ISO 18004 §8.6). */
  function addEccAndInterleave(data, ver) {
    var numBlocks = NUM_BLOCKS[ver];
    var blockEccLen = ECC_PER_BLOCK[ver];
    var rawCodewords = Math.floor(getNumRawDataModules(ver) / 8);
    var numShortBlocks = numBlocks - rawCodewords % numBlocks;
    var shortBlockLen = Math.floor(rawCodewords / numBlocks);

    var blocks = [];
    var divisor = rsComputeDivisor(blockEccLen);
    for (var i = 0, k = 0; i < numBlocks; i++) {
      var datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
      var dat = data.slice(k, k + datLen);
      k += datLen;
      var ecc = rsComputeRemainder(dat, divisor);
      if (i < numShortBlocks) dat.push(0); // placeholder to even the lengths
      blocks.push(dat.concat(ecc));
    }

    var result = [];
    for (var col = 0; col < blocks[0].length; col++) {
      for (var row = 0; row < blocks.length; row++) {
        // skip the padding byte of short blocks
        if (col !== shortBlockLen - blockEccLen || row >= numShortBlocks)
          result.push(blocks[row][col]);
      }
    }
    return result;
  }

  // ── matrix ─────────────────────────────────────────────────────────────────
  function getAlignmentPositions(ver) {
    if (ver === 1) return [];
    var numAlign = Math.floor(ver / 7) + 2;
    var size = ver * 4 + 17;
    var step = Math.ceil((ver * 4 + 4) / (numAlign * 2 - 2)) * 2;
    var result = [6];
    for (var pos = size - 7; result.length < numAlign; pos -= step)
      result.splice(1, 0, pos);
    return result;
  }

  function qrEncode(text) {
    var bytes = toUtf8Bytes(text);
    var ver = pickVersion(bytes.length);
    if (ver < 0) return null; // too long for v10-M

    var allCodewords = addEccAndInterleave(buildCodewords(bytes, ver), ver);
    var size = ver * 4 + 17;
    var modules = [];
    var isFunction = [];
    for (var y = 0; y < size; y++) {
      modules.push(new Array(size).fill(false));
      isFunction.push(new Array(size).fill(false));
    }

    function setFunction(x, y, dark) {
      if (x < 0 || x >= size || y < 0 || y >= size) return;
      modules[y][x] = dark;
      isFunction[y][x] = true;
    }
    function getBit(x, i) { return ((x >>> i) & 1) !== 0; }

    // timing patterns
    for (var t = 0; t < size; t++) {
      setFunction(6, t, t % 2 === 0);
      setFunction(t, 6, t % 2 === 0);
    }

    // finder patterns + separators
    [[3, 3], [size - 4, 3], [3, size - 4]].forEach(function (c) {
      for (var dy = -4; dy <= 4; dy++) {
        for (var dx = -4; dx <= 4; dx++) {
          var dist = Math.max(Math.abs(dx), Math.abs(dy));
          setFunction(c[0] + dx, c[1] + dy, dist !== 2 && dist !== 4);
        }
      }
    });

    // alignment patterns (skip the three finder corners)
    var align = getAlignmentPositions(ver);
    var n = align.length;
    for (var ai = 0; ai < n; ai++) {
      for (var aj = 0; aj < n; aj++) {
        if ((ai === 0 && aj === 0) || (ai === 0 && aj === n - 1) || (ai === n - 1 && aj === 0))
          continue;
        for (var ady = -2; ady <= 2; ady++)
          for (var adx = -2; adx <= 2; adx++)
            setFunction(align[ai] + adx, align[aj] + ady, Math.max(Math.abs(adx), Math.abs(ady)) !== 1);
      }
    }

    // format info — ECC M (= 0b00) with mask 0, BCH(15,5)
    var fmtData = 0; // (eccBits << 3) | mask
    var fmtRem = fmtData;
    for (var f = 0; f < 10; f++) fmtRem = (fmtRem << 1) ^ ((fmtRem >>> 9) * 0x537);
    var fmtBits = ((fmtData << 10) | fmtRem) ^ 0x5412;
    for (var fi = 0; fi <= 5; fi++) setFunction(8, fi, getBit(fmtBits, fi));
    setFunction(8, 7, getBit(fmtBits, 6));
    setFunction(8, 8, getBit(fmtBits, 7));
    setFunction(7, 8, getBit(fmtBits, 8));
    for (var fj = 9; fj < 15; fj++) setFunction(14 - fj, 8, getBit(fmtBits, fj));
    for (var fk = 0; fk < 8; fk++) setFunction(size - 1 - fk, 8, getBit(fmtBits, fk));
    for (var fl = 8; fl < 15; fl++) setFunction(8, size - 15 + fl, getBit(fmtBits, fl));
    setFunction(8, size - 8, true); // dark module

    // version info for v7+, BCH(18,6)
    if (ver >= 7) {
      var vRem = ver;
      for (var vi = 0; vi < 12; vi++) vRem = (vRem << 1) ^ ((vRem >>> 11) * 0x1f25);
      var vBits = (ver << 12) | vRem;
      for (var vb = 0; vb < 18; vb++) {
        var va = size - 11 + (vb % 3);
        var vc = Math.floor(vb / 3);
        setFunction(va, vc, getBit(vBits, vb));
        setFunction(vc, va, getBit(vBits, vb));
      }
    }

    // zigzag data placement
    var bitIndex = 0;
    for (var right = size - 1; right >= 1; right -= 2) {
      if (right === 6) right = 5;
      for (var vert = 0; vert < size; vert++) {
        for (var s = 0; s < 2; s++) {
          var px = right - s;
          var upward = ((right + 1) & 2) === 0;
          var py = upward ? size - 1 - vert : vert;
          if (!isFunction[py][px] && bitIndex < allCodewords.length * 8) {
            modules[py][px] = getBit(allCodewords[bitIndex >>> 3], 7 - (bitIndex & 7));
            bitIndex++;
          }
        }
      }
    }

    // mask 0: invert where (x + y) % 2 === 0
    for (var my = 0; my < size; my++)
      for (var mx = 0; mx < size; mx++)
        if (!isFunction[my][mx] && (mx + my) % 2 === 0)
          modules[my][mx] = !modules[my][mx];

    return modules;
  }

  if (typeof module !== "undefined" && module.exports)
    module.exports = { qrEncode: qrEncode };
  else
    global.qrEncode = qrEncode;
})(typeof window !== "undefined" ? window : this);
