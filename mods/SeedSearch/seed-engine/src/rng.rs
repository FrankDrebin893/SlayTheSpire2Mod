//! Port of MegaCrit.Sts2.Core.Random.Rng / MegaRandom (xoshiro256** seeded with splitmix64)
//! and StringHelper.GetDeterministicHashCode (XxHash64, seed 0, over UTF-8).

use xxhash_rust::xxh64::xxh64;

pub fn hash(s: &str) -> u64 {
    xxh64(s.as_bytes(), 0)
}

#[derive(Clone)]
pub struct Rng {
    s: [u64; 4],
}

fn splitmix64(x: &mut u64) -> u64 {
    *x = x.wrapping_add(0x9E37_79B9_7F4A_7C15);
    let mut z = *x;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

impl Rng {
    pub fn new(mut seed: u64) -> Self {
        let s0 = splitmix64(&mut seed);
        let s1 = splitmix64(&mut seed);
        let s2 = splitmix64(&mut seed);
        let s3 = splitmix64(&mut seed);
        Rng { s: [s0, s1, s2, s3] }
    }

    /// `new Rng(seed, name)`
    pub fn named(seed: u64, name: &str) -> Self {
        Self::new(seed.wrapping_add(hash(name)))
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        let [mut s0, mut s1, mut s2, mut s3] = self.s;
        let result = s1.wrapping_mul(5).rotate_left(7).wrapping_mul(9);
        let t = s1 << 17;
        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = s3.rotate_left(45);
        self.s = [s0, s1, s2, s3];
        result
    }

    #[inline]
    pub fn next_double(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 * 1.1102230246251565E-16
    }

    /// `NextInt(maxExclusive)`
    #[inline]
    pub fn next_int(&mut self, max: i32) -> i32 {
        (self.next_double() * max as f64) as i32
    }

    /// `NextInt(minInclusive, maxExclusive)`
    #[inline]
    pub fn next_int_range(&mut self, min: i32, max: i32) -> i32 {
        self.next_int(max - min) + min
    }

    /// `NextFloat()` with the default range [0, 1).
    #[inline]
    pub fn next_float(&mut self) -> f32 {
        self.next_double() as f32
    }

    #[inline]
    pub fn next_bool(&mut self) -> bool {
        self.next_int(2) == 0
    }

    /// `NextItem`: consumes nothing when the list is empty.
    pub fn next_index(&mut self, len: usize) -> Option<usize> {
        if len == 0 {
            None
        } else {
            Some(self.next_int_range(0, len as i32) as usize)
        }
    }

    pub fn next_item<T: Copy>(&mut self, items: &[T]) -> Option<T> {
        self.next_index(items.len()).map(|i| items[i])
    }

    pub fn next_gaussian_int(&mut self, mean: i32, std_dev: i32, min: i32, max: i32) -> i32 {
        loop {
            let d = 1.0 - self.next_double();
            let n = 1.0 - self.next_double();
            let z = (-2.0 * d.ln()).sqrt() * (std::f64::consts::PI * 2.0 * n).sin();
            let a = mean as f64 + std_dev as f64 * z;
            let r = a.round_ties_even() as i32;
            if r >= min && r <= max {
                return r;
            }
        }
    }

    /// `ListExtensions.UnstableShuffle` (and `Rng.Shuffle`, which is the same walk).
    pub fn shuffle<T>(&mut self, list: &mut [T]) {
        let mut n = list.len();
        while n > 1 {
            n -= 1;
            let k = self.next_int(n as i32 + 1) as usize;
            list.swap(k, n);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn xxhash_known_answers() {
        // Reference values of XXH64 with seed 0.
        assert_eq!(hash(""), 0xEF46_DB37_51D8_E999);
        assert_eq!(hash("a"), 0xD24E_C4F1_A98C_6E5B);
        assert_eq!(hash("abc"), 0x44BC_2CF5_AD77_0999);
    }

    #[test]
    fn splitmix_known_answers() {
        // First outputs of splitmix64 from state 0.
        let mut x = 0u64;
        assert_eq!(splitmix64(&mut x), 0xE220_A839_7B1D_CDAF);
        assert_eq!(splitmix64(&mut x), 0x6E78_9E6A_A1B9_65F4);
    }

    #[test]
    fn shuffle_is_a_permutation() {
        let mut rng = Rng::new(42);
        let mut v: Vec<u32> = (0..50).collect();
        rng.shuffle(&mut v);
        let mut sorted = v.clone();
        sorted.sort();
        assert_eq!(sorted, (0..50).collect::<Vec<_>>());
        assert_ne!(v, sorted);
    }
}
