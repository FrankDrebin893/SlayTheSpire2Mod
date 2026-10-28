//! Port of StandardActMap, MapPathPruning and MapPostProcessing.
//!
//! The game keeps parents/children in HashSet<MapPoint> (reference equality) and several steps
//! depend on the enumeration order of those sets, so `NetSet` reproduces how .NET's HashSet
//! orders its entries: insertion order, with a freed slot reused by the next insert.

use crate::data::{ActData, Roll, Snapshot};
use crate::rng::Rng;
use std::collections::{HashMap, VecDeque};
use std::hash::{BuildHasherDefault, Hasher};

pub const COLS: usize = 7;

pub const UNASSIGNED: u8 = 0;
pub const UNKNOWN: u8 = 1;
pub const SHOP: u8 = 2;
pub const TREASURE: u8 = 3;
pub const REST: u8 = 4;
pub const MONSTER: u8 = 5;
pub const ELITE: u8 = 6;
pub const BOSS: u8 = 7;
pub const ANCIENT: u8 = 8;

type P = usize;

#[derive(Default, Clone)]
struct NetSet {
    slots: Vec<Option<P>>,
    free: Vec<usize>,
}

impl NetSet {
    fn len(&self) -> usize {
        self.slots.len() - self.free.len()
    }

    fn contains(&self, p: P) -> bool {
        self.slots.contains(&Some(p))
    }

    fn add(&mut self, p: P) {
        if self.contains(p) {
            return;
        }
        match self.free.pop() {
            Some(i) => self.slots[i] = Some(p),
            None => self.slots.push(Some(p)),
        }
    }

    fn remove(&mut self, p: P) {
        if let Some(i) = self.slots.iter().position(|s| *s == Some(p)) {
            self.slots[i] = None;
            self.free.push(i);
        }
    }

    fn iter(&self) -> impl Iterator<Item = P> + '_ {
        self.slots.iter().flatten().copied()
    }

    fn first(&self) -> Option<P> {
        self.iter().next()
    }
}

struct Point {
    col: i32,
    row: i32,
    ty: u8,
    can_modify: bool,
    parents: NetSet,
    children: NetSet,
}

pub struct MapOut {
    /// (col, row, type) of every point in the grid, column by column.
    pub points: Vec<(i32, i32, u8)>,
    /// (col, row) -> (col, row) between grid points.
    pub edges: Vec<(i32, i32, i32, i32)>,
}

impl MapOut {
    pub fn count(&self, ty: u8) -> i32 {
        self.points.iter().filter(|p| p.2 == ty).count() as i32
    }
}

struct Counts {
    rests: i32,
    shops: i32,
    elites: i32,
    unknowns: i32,
}

struct Gen<'a> {
    rng: &'a mut Rng,
    pts: Vec<Point>,
    grid: Vec<Vec<Option<P>>>,
    map_len: i32,
    start: P,
    boss: P,
    start_points: Vec<P>,
}

fn roll(rng: &mut Rng, r: Roll) -> i32 {
    match r {
        Roll::Gaussian { mean, std, min, max } => rng.next_gaussian_int(mean, std, min, max),
        Roll::Int { min, max } => rng.next_int_range(min, max),
    }
}

/// `new StandardActMap(new Rng(seed, "act_N_map"), act, false, false)`.
/// Returns None where the game would throw.
pub fn generate(snap: &Snapshot, seed: u64, slot: usize, act: &ActData) -> Option<MapOut> {
    let mut rng = Rng::named(seed, &format!("act_{}_map", slot + 1));
    let map_len = act.rooms + 1;
    if map_len < 8 {
        return None;
    }

    // ActModel.GetMapPointTypes
    let rests = roll(&mut rng, act.rest);
    let unknowns = rng.next_gaussian_int(12, 1, 10, 14) + act.unknown_offset;
    let counts = Counts { rests, shops: snap.map_shops, elites: snap.map_elites, unknowns };

    let mut g = Gen {
        rng: &mut rng,
        pts: Vec::with_capacity(96),
        grid: vec![vec![None; map_len as usize]; COLS],
        map_len,
        start: 0,
        boss: 0,
        start_points: Vec::new(),
    };
    g.boss = g.new_point(COLS as i32 / 2, map_len);
    g.start = g.new_point(COLS as i32 / 2, 0);

    g.generate_map()?;
    g.assign_point_types(&counts);
    g.prune_and_repair(&counts)?;
    g.center_grid();
    g.spread_adjacent();
    g.straighten_paths();
    Some(g.output())
}

impl Gen<'_> {
    fn new_point(&mut self, col: i32, row: i32) -> P {
        self.pts.push(Point { col, row, ty: UNASSIGNED, can_modify: true, parents: NetSet::default(), children: NetSet::default() });
        self.pts.len() - 1
    }

    fn at(&self, col: i32, row: i32) -> Option<P> {
        *self.grid.get(col as usize)?.get(row as usize)?
    }

    fn get_or_create(&mut self, col: i32, row: i32) -> P {
        if let Some(p) = self.at(col, row) {
            return p;
        }
        let p = self.new_point(col, row);
        self.grid[col as usize][row as usize] = Some(p);
        p
    }

    fn add_child(&mut self, parent: P, child: P) {
        self.pts[parent].children.add(child);
        self.pts[child].parents.add(parent);
    }

    fn remove_child(&mut self, parent: P, child: P) {
        self.pts[parent].children.remove(child);
        self.pts[child].parents.remove(parent);
    }

    /// ActMap.GetAllMapPoints: column by column, which is also MapPoint's sort order.
    fn all_points(&self) -> Vec<P> {
        let mut out = Vec::with_capacity(self.pts.len());
        for col in &self.grid {
            out.extend(col.iter().flatten().copied());
        }
        out
    }

    fn row_points(&self, row: i32) -> Vec<P> {
        (0..COLS as i32).filter_map(|c| self.at(c, row)).collect()
    }

    // ---- StandardActMap.GenerateMap ----

    fn generate_map(&mut self) -> Option<()> {
        for i in 0..7 {
            let col = self.rng.next_int_range(0, 7);
            let mut p = self.get_or_create(col, 1);
            if i == 1 {
                while self.start_points.contains(&p) {
                    let col = self.rng.next_int_range(0, 7);
                    p = self.get_or_create(col, 1);
                }
            }
            if !self.start_points.contains(&p) {
                self.start_points.push(p);
            }
            self.path_generate(p)?;
        }
        for p in self.row_points(self.map_len - 1) {
            self.add_child(p, self.boss);
        }
        for p in self.row_points(1) {
            self.add_child(self.start, p);
        }
        Some(())
    }

    fn path_generate(&mut self, from: P) -> Option<()> {
        let mut cur = from;
        while self.pts[cur].row < self.map_len - 1 {
            let (col, row) = self.next_coord(cur)?;
            let next = self.get_or_create(col, row);
            self.add_child(cur, next);
            cur = next;
        }
        Some(())
    }

    fn next_coord(&mut self, cur: P) -> Option<(i32, i32)> {
        let col = self.pts[cur].col;
        let left = (col - 1).max(0);
        let right = (col + 1).min(6);
        // StableShuffle of an already sorted list.
        let mut dirs = [-1, 0, 1];
        self.rng.shuffle(&mut dirs);
        for d in dirs {
            let target = match d {
                -1 => left,
                0 => col,
                _ => right,
            };
            if !self.has_invalid_crossover(cur, target) {
                return Some((target, self.pts[cur].row + 1));
            }
        }
        None
    }

    fn has_invalid_crossover(&self, cur: P, target_col: i32) -> bool {
        let d = target_col - self.pts[cur].col;
        if d == 0 || d == 7 {
            return false;
        }
        let Some(neighbour) = self.at(target_col, self.pts[cur].row) else { return false };
        let n = &self.pts[neighbour];
        n.children.iter().any(|c| self.pts[c].col - n.col == -d)
    }

    // ---- StandardActMap.AssignPointTypes ----

    fn assign_point_types(&mut self, counts: &Counts) {
        for (row, ty) in [(self.map_len - 1, REST), (self.map_len - 7, TREASURE), (1, MONSTER)] {
            for p in self.row_points(row) {
                self.pts[p].ty = ty;
                self.pts[p].can_modify = false;
            }
        }

        let mut queue: VecDeque<u8> = VecDeque::new();
        for (n, ty) in [(counts.rests, REST), (counts.shops, SHOP), (counts.elites, ELITE), (counts.unknowns, UNKNOWN)] {
            for _ in 0..n {
                queue.push_back(ty);
            }
        }

        // AssignRemainingTypesToRandomPoints
        for _ in 0..3 {
            if queue.is_empty() {
                break;
            }
            let mut list: Vec<P> = self.all_points().into_iter().filter(|&p| self.pts[p].ty == UNASSIGNED).collect();
            self.rng.shuffle(&mut list);
            for p in list {
                if queue.is_empty() {
                    break;
                }
                self.pts[p].ty = self.next_valid_type(&mut queue, p);
            }
        }

        for p in self.all_points() {
            if self.pts[p].ty == UNASSIGNED {
                self.pts[p].ty = MONSTER;
            }
        }
        self.pts[self.boss].ty = BOSS;
        self.pts[self.start].ty = ANCIENT;
    }

    fn next_valid_type(&self, queue: &mut VecDeque<u8>, p: P) -> u8 {
        for _ in 0..queue.len() {
            let Some(ty) = queue.pop_front() else { break };
            if self.is_valid_type(ty, p) {
                return ty;
            }
            queue.push_back(ty);
        }
        UNASSIGNED
    }

    fn is_valid_type(&self, ty: u8, p: P) -> bool {
        let pt = &self.pts[p];
        // IsValidForUpper
        if pt.row >= self.map_len - 3 && ty == REST {
            return false;
        }
        // IsValidForLower
        if pt.row < 6 && (ty == REST || ty == ELITE) {
            return false;
        }
        // IsValidWithParents / IsValidWithChildren
        if matches!(ty, ELITE | REST | TREASURE | SHOP) && pt.parents.iter().chain(pt.children.iter()).any(|q| self.pts[q].ty == ty) {
            return false;
        }
        // IsValidWithSiblings
        if matches!(ty, REST | MONSTER | UNKNOWN | ELITE | SHOP) {
            for parent in pt.parents.iter() {
                if self.pts[parent].children.iter().any(|s| s != p && self.pts[s].ty == ty) {
                    return false;
                }
            }
        }
        true
    }

    // ---- MapPathPruning ----

    fn prune_and_repair(&mut self, counts: &Counts) -> Option<()> {
        for _ in 0..3 {
            self.prune_duplicate_segments()?;
            let mut repaired = false;
            for (ty, target) in [(SHOP, counts.shops), (ELITE, counts.elites), (REST, counts.rests), (UNKNOWN, counts.unknowns)] {
                repaired |= self.repair_point_type(ty, target);
            }
            if !repaired {
                break;
            }
        }
        Some(())
    }

    fn repair_point_type(&mut self, ty: u8, target: i32) -> bool {
        let all = self.all_points();
        let have = all.iter().filter(|&&p| self.pts[p].ty == ty).count() as i32;
        let mut missing = target - have;
        if missing <= 0 {
            return false;
        }
        let mut list: Vec<P> = all.into_iter().filter(|&p| self.pts[p].ty == MONSTER && self.pts[p].can_modify).collect();
        self.rng.shuffle(&mut list);
        let mut changed = false;
        for p in list {
            if missing == 0 {
                break;
            }
            if self.is_valid_type(ty, p) {
                self.pts[p].ty = ty;
                missing -= 1;
                changed = true;
            }
        }
        changed
    }

    fn prune_duplicate_segments(&mut self) -> Option<()> {
        let mut iterations = 0;
        let mut matching = self.find_matching_segments();
        while self.prune_paths(&mut matching) {
            iterations += 1;
            if iterations > 50 {
                return None;
            }
            matching = self.find_matching_segments();
        }
        Some(())
    }

    fn segment_key(&self, seg: &[P]) -> String {
        use std::fmt::Write;
        let a = &self.pts[seg[0]];
        let b = &self.pts[seg[seg.len() - 1]];
        let mut key = String::with_capacity(16 + seg.len() * 2);
        if a.row == 0 {
            let _ = write!(key, "{}-{},{}-", a.row, b.col, b.row);
        } else {
            let _ = write!(key, "{},{}-{},{}-", a.col, a.row, b.col, b.row);
        }
        for (i, &p) in seg.iter().enumerate() {
            if i > 0 {
                key.push(',');
            }
            let _ = write!(key, "{}", self.pts[p].ty);
        }
        key
    }

    /// MapPathPruning.FindMatchingSegments.
    ///
    /// The game lists every start-to-boss path and takes every sub-path of each as a candidate
    /// segment, so a segment shared by many paths is looked at again and again; repeats change
    /// nothing, because a segment always overlaps itself. Segments with the same key share their
    /// first and last point, and the first path containing a given segment is the first path to
    /// its starting point followed by it, so walking each starting point's descendants depth
    /// first visits the segments of every key in the game's order, once each.
    fn find_matching_segments(&self) -> Vec<Vec<Vec<P>>> {
        let mut reachable = vec![false; self.pts.len()];
        let mut stack = vec![self.start];
        while let Some(p) = stack.pop() {
            if !std::mem::replace(&mut reachable[p], true) {
                stack.extend(self.pts[p].children.iter());
            }
        }

        // Collected under a compact key (first point, last point, the point types packed four
        // bits each); only the groups that survive get the game's string key, which decides
        // the order they are pruned in.
        let mut segments: HashMap<(P, P, u128), Vec<Vec<P>>, BuildHasherDefault<KeyHasher>> = HashMap::default();
        let mut path = Vec::with_capacity(self.map_len as usize + 2);
        for start in 0..self.pts.len() {
            let pt = &self.pts[start];
            if reachable[start] && (pt.children.len() > 1 || pt.row == 0) {
                self.collect_segments(start, 0, &mut path, &mut segments);
            }
        }

        // SortedDictionary<string, ...>(StringComparer.Ordinal); the keys are ASCII.
        let mut duplicates: Vec<(String, Vec<Vec<P>>)> =
            segments.into_values().filter(|l| l.len() > 1).map(|l| (self.segment_key(&l[0]), l)).collect();
        duplicates.sort_unstable_by(|a, b| a.0.cmp(&b.0));
        duplicates.into_iter().map(|(_, l)| l).collect()
    }

    fn collect_segments(&self, cur: P, types: u128, path: &mut Vec<P>, segments: &mut HashMap<(P, P, u128), Vec<Vec<P>>, BuildHasherDefault<KeyHasher>>) {
        let pt = &self.pts[cur];
        let types = types << 4 | pt.ty as u128;
        path.push(cur);
        if path.len() >= 3 && pt.parents.len() >= 2 {
            let list = segments.entry((path[0], cur, types)).or_default();
            if !list.iter().any(|existing| overlapping(existing, path)) {
                list.push(path.clone());
            }
        }
        if pt.ty != BOSS {
            for child in pt.children.iter() {
                self.collect_segments(child, types, path, segments);
            }
        }
        path.pop();
    }

    fn prune_paths(&mut self, matching: &mut [Vec<Vec<P>>]) -> bool {
        for matches in matching.iter_mut() {
            self.rng.shuffle(matches);
            if self.prune_all_but_last(matches) != 0 {
                return true;
            }
            if matches.iter().any(|seg| self.break_relationship_in_segment(seg)) {
                return true;
            }
        }
        false
    }

    fn prune_all_but_last(&mut self, matches: &[Vec<P>]) -> usize {
        let mut pruned = 0;
        for seg in matches {
            if pruned == matches.len() - 1 {
                return pruned;
            }
            if self.prune_segment(seg) {
                pruned += 1;
            }
        }
        pruned
    }

    fn is_removed(&self, p: P) -> bool {
        self.at(self.pts[p].col, self.pts[p].row).is_none()
    }

    fn is_in_map(&self, p: P) -> bool {
        if self.is_removed(p) && self.pts[p].ty != ANCIENT {
            return self.pts[p].ty == BOSS;
        }
        true
    }

    fn prune_segment(&mut self, seg: &[P]) -> bool {
        let mut result = false;
        for i in 0..seg.len() - 1 {
            let p = seg[i];
            if !self.is_in_map(p) {
                return true;
            }
            let pt = &self.pts[p];
            if pt.children.len() > 1
                || pt.parents.len() > 1
                || pt.parents.iter().any(|n| self.pts[n].children.len() == 1 && !self.is_removed(n))
            {
                continue;
            }
            if seg[i..].iter().any(|&n| self.pts[n].children.len() > 1 && self.pts[n].parents.len() == 1) {
                continue;
            }
            if self.pts[seg[seg.len() - 1]].parents.len() == 1 {
                return false;
            }
            if !pt.children.iter().filter(|c| !seg.contains(c)).any(|c| self.pts[c].parents.len() == 1) {
                self.remove_point(p);
                result = true;
            }
        }
        result
    }

    fn remove_point(&mut self, p: P) {
        let (col, row) = (self.pts[p].col, self.pts[p].row);
        self.grid[col as usize][row as usize] = None;
        self.start_points.retain(|&s| s != p);
        for child in self.pts[p].children.iter().collect::<Vec<_>>() {
            self.remove_child(p, child);
        }
        for parent in self.pts[p].parents.iter().collect::<Vec<_>>() {
            self.remove_child(parent, p);
        }
    }

    fn break_relationship_in_segment(&mut self, seg: &[P]) -> bool {
        let mut result = false;
        for i in 0..seg.len() - 1 {
            let (p, next) = (seg[i], seg[i + 1]);
            if self.pts[p].children.len() >= 2 && self.pts[next].parents.len() != 1 {
                self.remove_child(p, next);
                result = true;
            }
        }
        result
    }

    // ---- MapPostProcessing ----

    fn column_empty(&self, col: usize) -> bool {
        self.grid[col].iter().all(|p| p.is_none())
    }

    fn move_point(&mut self, p: P, row: i32, to_col: i32) {
        let from = self.pts[p].col;
        self.grid[from as usize][row as usize] = None;
        self.grid[to_col as usize][row as usize] = Some(p);
        self.pts[p].col = to_col;
    }

    fn center_grid(&mut self) {
        let left_empty = self.column_empty(0) && self.column_empty(1);
        let right_empty = self.column_empty(COLS - 1) && self.column_empty(COLS - 2);
        let shift: i32 = if left_empty && !right_empty {
            -1
        } else if !left_empty && right_empty {
            1
        } else {
            return;
        };
        for row in 0..self.map_len as usize {
            let old: Vec<Option<P>> = (0..COLS).map(|c| self.grid[c][row]).collect();
            for c in 0..COLS {
                self.grid[c][row] = None;
            }
            for (c, p) in old.into_iter().enumerate() {
                let target = c as i32 + shift;
                if let (Some(p), true) = (p, target >= 0 && target < COLS as i32) {
                    self.grid[target as usize][row] = Some(p);
                    self.pts[p].col = target;
                }
            }
        }
    }

    fn allowed_positions(&self, p: P) -> Vec<i32> {
        let pt = &self.pts[p];
        (0..COLS as i32)
            .filter(|&c| pt.parents.iter().chain(pt.children.iter()).all(|n| (c - self.pts[n].col).abs() <= 1))
            .collect()
    }

    fn compute_gap(&self, candidate: i32, row_nodes: &[P], current: P) -> i32 {
        row_nodes.iter().filter(|&&n| n != current).map(|&n| (candidate - self.pts[n].col).abs()).min().unwrap_or(i32::MAX)
    }

    fn spread_adjacent(&mut self) {
        for row in 0..self.map_len {
            let nodes = self.row_points(row);
            loop {
                let mut moved = false;
                for &p in &nodes {
                    let col = self.pts[p].col;
                    let mut best_col = col;
                    let mut best_gap = self.compute_gap(col, &nodes, p);
                    for c in self.allowed_positions(p) {
                        if c != col && self.at(c, row).map_or(true, |q| q == p) {
                            let gap = self.compute_gap(c, &nodes, p);
                            if gap > best_gap {
                                best_col = c;
                                best_gap = gap;
                            }
                        }
                    }
                    if best_col != col {
                        self.move_point(p, row, best_col);
                        moved = true;
                    }
                }
                if !moved {
                    break;
                }
            }
        }
    }

    fn straighten_paths(&mut self) {
        for row in 0..self.map_len {
            for c in 0..COLS as i32 {
                let Some(p) = self.at(c, row) else { continue };
                let pt = &self.pts[p];
                if pt.parents.len() != 1 || pt.children.len() != 1 {
                    continue;
                }
                let (Some(parent), Some(child)) = (pt.parents.first(), pt.children.first()) else { continue };
                let (pc, cc) = (self.pts[parent].col, self.pts[child].col);
                let bends_left = pt.col < cc && pt.col < pc;
                let bends_right = pt.col > cc && pt.col > pc;
                if bends_left && c < COLS as i32 - 1 {
                    if self.at(c + 1, row).is_some() {
                        continue;
                    }
                    self.move_point(p, row, c + 1);
                }
                if bends_right && c > 0 && self.at(c - 1, row).is_none() {
                    self.move_point(p, row, c - 1);
                }
            }
        }
    }

    fn output(&self) -> MapOut {
        let all = self.all_points();
        let points = all.iter().map(|&p| (self.pts[p].col, self.pts[p].row, self.pts[p].ty)).collect();
        let mut edges = Vec::new();
        for &p in &all {
            for c in self.pts[p].children.iter() {
                if c != self.boss {
                    edges.push((self.pts[p].col, self.pts[p].row, self.pts[c].col, self.pts[c].row));
                }
            }
        }
        edges.sort_unstable();
        MapOut { points, edges }
    }
}

/// MapPathPruning.OverlappingSegment
fn overlapping(a: &[P], b: &[P]) -> bool {
    if a.len() < 3 || b.len() < 3 {
        return false;
    }
    (1..=a.len() - 2).any(|i| b.get(i) == Some(&a[i]))
}

/// A cheap hasher for the small integer keys of the segment table.
#[derive(Default)]
struct KeyHasher(u64);

impl Hasher for KeyHasher {
    fn finish(&self) -> u64 {
        self.0
    }

    fn write(&mut self, bytes: &[u8]) {
        for chunk in bytes.chunks(8) {
            let mut word = [0u8; 8];
            word[..chunk.len()].copy_from_slice(chunk);
            self.0 = (self.0.rotate_left(5) ^ u64::from_le_bytes(word)).wrapping_mul(0x517C_C1B7_2722_0A95);
        }
    }
}
