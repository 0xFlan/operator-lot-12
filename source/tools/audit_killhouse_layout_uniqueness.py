#!/usr/bin/env python3
"""Audit Kill House scene variants for substantive layout uniqueness.

The audit is deliberately independent of variant names and motif labels.  It
uses the generated scene-validation records plus the actual room transforms in
the serialized Unity scenes.  Signatures are invariant to room-number changes,
translation, 90-degree rotation, and reflection.
"""

from __future__ import annotations

import argparse
import collections
import datetime as dt
import hashlib
import itertools
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

SOURCE_ROOT = Path(__file__).resolve().parents[1]
MATRIX_PATH = SOURCE_ROOT / "design" / "killhouse_variant_matrix.json"
VALIDATION_DIR = SOURCE_ROOT / "evidence" / "scene-validation"
SCENE_ROOT = SOURCE_ROOT / "unity_project" / "Assets" / "VektorKillHouse" / "Scenes"
BUILDER_PATH = (
    SOURCE_ROOT
    / "unity_project"
    / "Assets"
    / "VektorKillHouse"
    / "Editor"
    / "KillHouseVariantBuilder.cs"
)
DEFAULT_JSON = SOURCE_ROOT / "evidence" / "killhouse_layout_uniqueness_audit.json"
DEFAULT_MARKDOWN = SOURCE_ROOT / "evidence" / "killhouse_layout_uniqueness_audit.md"
EXPECTED_MATRIX_SCHEMA = "vektor-killhouse/variant-matrix@16"
EXPECTED_SCENE_VALIDATION_SCHEMA = "vektor-killhouse/scene-validation@20"


# These are prospective design-quality floors, not variant-name checks. A pair
# may share one individual trait, but must remain distinct in complete topology,
# physical placement, and at least three independent layout axes.
THRESHOLDS = {
    "maximumExactUnattributedTopologyIsomorphicPairs": 0,
    "maximumExactSpatialTopologyDuplicatePairs": 0,
    "maximumExactAttributedSpatialDuplicatePairs": 0,
    "maximumCompositeSimilarity": 0.78,
    "minimumMeaningfullyDifferentAxesPerPair": 3,
    "minimumDihedralRoomSequenceEdits": 4,
    "minimumDihedralCyclePortalEdits": 1,
}


D4 = (
    (1, 0, 0, 1),
    (1, 0, 0, -1),
    (-1, 0, 0, 1),
    (-1, 0, 0, -1),
    (0, 1, 1, 0),
    (0, 1, -1, 0),
    (0, -1, 1, 0),
    (0, -1, -1, 0),
)


@dataclass(frozen=True)
class Edge:
    u: int
    v: int
    portal: str
    axis: str
    offset: float


@dataclass
class GraphData:
    node_attrs: dict[int, dict[str, Any]]
    adjacency: dict[int, set[int]]
    edge_attrs: dict[tuple[int, int], dict[str, Any]]

    def nodes(self) -> list[int]:
        return sorted(self.node_attrs)

    def neighbors(self, node: int) -> set[int]:
        return self.adjacency[node]

    def degree(self, node: int) -> int:
        return len(self.adjacency[node])

    def has_edge(self, first: int, second: int) -> bool:
        return second in self.adjacency[first]

    def edge_attr(self, first: int, second: int, name: str) -> Any:
        return self.edge_attrs[(min(first, second), max(first, second))][name]

    def number_of_nodes(self) -> int:
        return len(self.node_attrs)

    def number_of_edges(self) -> int:
        return len(self.edge_attrs)


@dataclass
class VariantData:
    id: str
    index: int
    scene_path: Path
    matrix: dict[str, Any]
    validation: dict[str, Any]
    room_types: list[str]
    room_sizes: list[tuple[float, float]]
    room_positions: dict[int, tuple[float, float]]
    edges: list[Edge]
    graph: GraphData
    topology_wl_hash: str
    attributed_wl_hash: str
    spatial_topology_signature: str
    attributed_spatial_signature: str
    portal_spatial_signature: str
    primary_cycle_shape_signature: str


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_path(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def quantize(value: float) -> int:
    return int(round(value * 1000.0))


def transform_point(point: tuple[float, float], matrix: tuple[int, int, int, int]) -> tuple[float, float]:
    x, z = point
    a, b, c, d = matrix
    return a * x + b * z, c * x + d * z


def transform_size(size: tuple[float, float], matrix: tuple[int, int, int, int]) -> tuple[float, float]:
    width, depth = size
    return (depth, width) if matrix[1] or matrix[2] else (width, depth)


def parse_room_positions(scene_path: Path) -> dict[int, tuple[float, float]]:
    text = scene_path.read_text(encoding="utf-8")
    blocks = re.split(r"(?=^--- !u!)", text, flags=re.MULTILINE)
    transform_by_room: dict[int, str] = {}
    transform_positions: dict[str, tuple[float, float]] = {}

    for block in blocks:
        header = re.match(r"--- !u!(\d+) &(\d+)", block)
        if not header:
            continue
        class_id, object_id = header.groups()
        if class_id == "1":
            room_match = re.search(r"^\s*m_Name: ROOM_(\d{2})_(.+)$", block, flags=re.MULTILINE)
            component_match = re.search(r"^\s*- component: \{fileID: (\d+)\}$", block, flags=re.MULTILINE)
            if room_match and component_match:
                transform_by_room[int(room_match.group(1))] = component_match.group(1)
        elif class_id == "4":
            position_match = re.search(
                r"^\s*m_LocalPosition: \{x: ([^,]+), y: ([^,]+), z: ([^}]+)\}$",
                block,
                flags=re.MULTILINE,
            )
            if position_match:
                transform_positions[object_id] = (
                    float(position_match.group(1)),
                    float(position_match.group(3)),
                )

    missing = sorted(room for room, transform in transform_by_room.items() if transform not in transform_positions)
    if missing:
        raise ValueError(f"{scene_path.name}: room transforms missing positions: {missing}")
    return {room: transform_positions[transform] for room, transform in transform_by_room.items()}


def parse_size(value: str) -> tuple[float, float]:
    width, depth = value.lower().split("x", 1)
    return float(width), float(depth)


def load_builder_definitions() -> dict[str, dict[str, str]]:
    text = BUILDER_PATH.read_text(encoding="utf-8")
    pattern = re.compile(
        r'V\("(?P<id>[^"]+)",\s*"(?P<scene>[^"]+)",\s*"(?P<moves>[ENSW]+)",\s*'
        r'(?P<portal>.*?),\s*LayoutMotif\.(?P<motif>\w+),',
        flags=re.DOTALL,
    )
    result: dict[str, dict[str, str]] = {}
    for match in pattern.finditer(text):
        portal_literal = re.search(r'"([DO ]+)"', match.group("portal"))
        if not portal_literal:
            raise ValueError(f"Could not parse portal pattern for {match.group('id')}")
        result[match.group("id")] = {
            "sceneName": match.group("scene"),
            "moves": match.group("moves"),
            "portals": portal_literal.group(1).replace(" ", ""),
            "motif": match.group("motif"),
        }
    return result


def make_graph(
    room_types: list[str], room_sizes: list[tuple[float, float]], edges: list[Edge], base_cycle_count: int
) -> GraphData:
    node_attrs: dict[int, dict[str, Any]] = {}
    adjacency: dict[int, set[int]] = {index: set() for index in range(len(room_types))}
    edge_attrs: dict[tuple[int, int], dict[str, Any]] = {}
    for index, (room_type, size) in enumerate(zip(room_types, room_sizes)):
        size_unordered = tuple(sorted(size))
        node_attrs[index] = {
            "roomType": room_type,
            "size": f"{size_unordered[0]:.1f}x{size_unordered[1]:.1f}",
            "attributedLabel": f"{room_type}|{size_unordered[0]:.1f}x{size_unordered[1]:.1f}",
            "primaryCycle": index < base_cycle_count,
        }
    for edge in edges:
        key = (min(edge.u, edge.v), max(edge.u, edge.v))
        if key in edge_attrs:
            raise ValueError(f"Duplicate graph edge: {key}")
        adjacency[edge.u].add(edge.v)
        adjacency[edge.v].add(edge.u)
        edge_attrs[key] = {"portal": edge.portal}
    return GraphData(node_attrs=node_attrs, adjacency=adjacency, edge_attrs=edge_attrs)


def permute_graph(graph: GraphData, mapping: dict[int, int]) -> GraphData:
    if set(mapping) != set(graph.nodes()) or set(mapping.values()) != set(graph.nodes()):
        raise ValueError("Graph permutation must be a bijection over every node")
    node_attrs = {mapping[node]: dict(attributes) for node, attributes in graph.node_attrs.items()}
    adjacency = {mapping[node]: {mapping[neighbor] for neighbor in neighbors} for node, neighbors in graph.adjacency.items()}
    edge_attrs = {
        (min(mapping[first], mapping[second]), max(mapping[first], mapping[second])): dict(attributes)
        for (first, second), attributes in graph.edge_attrs.items()
    }
    return GraphData(node_attrs=node_attrs, adjacency=adjacency, edge_attrs=edge_attrs)


def portal_world_position(data: VariantData, edge: Edge) -> tuple[float, float]:
    first = data.room_positions[edge.u]
    second = data.room_positions[edge.v]
    if edge.axis.upper() == "X":
        return (first[0] + second[0]) * 0.5, first[1] + edge.offset
    return first[0] + edge.offset, (first[1] + second[1]) * 0.5


def canonical_spatial_payload(
    data: VariantData, include_attributes: bool, include_portals: bool
) -> tuple[str, str]:
    candidates: list[str] = []
    for matrix in D4:
        transformed_positions = {
            node: transform_point(position, matrix) for node, position in data.room_positions.items()
        }
        minimum_x = min(position[0] for position in transformed_positions.values())
        minimum_z = min(position[1] for position in transformed_positions.values())
        normalized_positions = {
            node: (quantize(position[0] - minimum_x), quantize(position[1] - minimum_z))
            for node, position in transformed_positions.items()
        }
        ordered_nodes = sorted(normalized_positions, key=lambda node: normalized_positions[node])
        rank = {node: ordinal for ordinal, node in enumerate(ordered_nodes)}
        nodes: list[Any] = []
        for node in ordered_nodes:
            record: list[Any] = [*normalized_positions[node]]
            if include_attributes:
                size = transform_size(data.room_sizes[node], matrix)
                record.extend((data.room_types[node], quantize(size[0]), quantize(size[1])))
            nodes.append(record)

        edges: list[Any] = []
        for edge in data.edges:
            record: list[Any] = [min(rank[edge.u], rank[edge.v]), max(rank[edge.u], rank[edge.v])]
            if include_portals:
                portal = transform_point(portal_world_position(data, edge), matrix)
                record.extend(
                    (
                        edge.portal,
                        quantize(portal[0] - minimum_x),
                        quantize(portal[1] - minimum_z),
                    )
                )
            edges.append(record)
        payload = json.dumps({"nodes": nodes, "edges": sorted(edges)}, separators=(",", ":"))
        candidates.append(payload)

    canonical = min(candidates)
    return sha256_bytes(canonical.encode("utf-8")), canonical


def cycle_cells(moves: str) -> set[tuple[int, int]]:
    cells = {(0, 0)}
    x = z = 0
    delta = {"E": (1, 0), "W": (-1, 0), "N": (0, 1), "S": (0, -1)}
    for index, move in enumerate(moves):
        dx, dz = delta[move]
        x += dx
        z += dz
        if index < len(moves) - 1:
            cells.add((x, z))
    if (x, z) != (0, 0):
        raise ValueError(f"Cycle does not close: {moves}")
    return cells


def transformed_cell_sets(cells: set[tuple[int, int]]) -> list[frozenset[tuple[int, int]]]:
    transformed: list[frozenset[tuple[int, int]]] = []
    for matrix in D4:
        points = [transform_point((float(x), float(z)), matrix) for x, z in cells]
        min_x = min(point[0] for point in points)
        min_z = min(point[1] for point in points)
        transformed.append(frozenset((int(point[0] - min_x), int(point[1] - min_z)) for point in points))
    return transformed


def canonical_cycle_shape(moves: str) -> str:
    payload = min(";".join(f"{x},{z}" for x, z in sorted(shape)) for shape in transformed_cell_sets(cycle_cells(moves)))
    return sha256_bytes(payload.encode("utf-8"))


def cycle_shape_similarity(first: str, second: str) -> float:
    first_shape = cycle_cells(first)
    best = 0.0
    for transformed in transformed_cell_sets(cycle_cells(second)):
        # Translation was normalized independently in transformed_cell_sets.
        normalized_first = transformed_cell_sets(first_shape)[0]
        union = len(normalized_first | transformed)
        best = max(best, len(normalized_first & transformed) / union if union else 1.0)
    return best


def levenshtein(first: list[str], second: list[str]) -> int:
    previous = list(range(len(second) + 1))
    for row, left in enumerate(first, start=1):
        current = [row]
        for column, right in enumerate(second, start=1):
            current.append(
                min(
                    current[-1] + 1,
                    previous[column] + 1,
                    previous[column - 1] + (left != right),
                )
            )
        previous = current
    return previous[-1]


def dihedral_sequence_distance(first: list[str], second: list[str]) -> int:
    return min(levenshtein(first, second), levenshtein(first, list(reversed(second))))


def dihedral_hamming(first: str, second: str) -> int:
    if len(first) != len(second):
        raise ValueError("Portal strings must have the same length")
    return min(
        sum(left != right for left, right in zip(first, second)),
        sum(left != right for left, right in zip(first, reversed(second))),
    )


def multiset_jaccard(first: collections.Counter[Any], second: collections.Counter[Any]) -> float:
    keys = first.keys() | second.keys()
    intersection = sum(min(first[key], second[key]) for key in keys)
    union = sum(max(first[key], second[key]) for key in keys)
    return intersection / union if union else 1.0


def initial_node_label(graph: GraphData, node: int, attributed: bool) -> tuple[Any, ...]:
    if not attributed:
        return ("degree", graph.degree(node))
    attributes = graph.node_attrs[node]
    return (
        attributes["roomType"],
        attributes["size"],
        attributes["primaryCycle"],
        graph.degree(node),
    )


def wl_features(graph: GraphData, rounds: int = 4, attributed: bool = False) -> collections.Counter[str]:
    labels = {
        node: sha256_bytes(repr(initial_node_label(graph, node, attributed)).encode("utf-8"))[:16]
        for node in graph.nodes()
    }
    features: collections.Counter[str] = collections.Counter()
    for iteration in range(rounds + 1):
        features.update(f"{iteration}:{label}" for label in labels.values())
        if iteration == rounds:
            break
        next_labels: dict[int, str] = {}
        for node in graph.nodes():
            neighbor_labels = []
            for neighbor in graph.neighbors(node):
                edge_label = graph.edge_attr(node, neighbor, "portal") if attributed else ""
                neighbor_labels.append(f"{edge_label}:{labels[neighbor]}")
            material = labels[node] + "|" + "|".join(sorted(neighbor_labels))
            next_labels[node] = sha256_bytes(material.encode("utf-8"))[:16]
        labels = next_labels
    return features


def wl_hash(graph: GraphData, rounds: int = 6, attributed: bool = False) -> str:
    features = wl_features(graph, rounds=rounds, attributed=attributed)
    payload = json.dumps(sorted(features.items()), separators=(",", ":"))
    return sha256_bytes(payload.encode("utf-8"))


def joint_color_refinement(
    first: GraphData, second: GraphData, attributed: bool
) -> tuple[dict[int, int], dict[int, int]]:
    graphs = (first, second)
    signatures = {
        (graph_index, node): initial_node_label(graph, node, attributed)
        for graph_index, graph in enumerate(graphs)
        for node in graph.nodes()
    }

    def compact(values: dict[tuple[int, int], tuple[Any, ...]]) -> dict[tuple[int, int], int]:
        palette = {signature: index for index, signature in enumerate(sorted(set(values.values()), key=repr))}
        return {key: palette[signature] for key, signature in values.items()}

    colors = compact(signatures)
    previous_class_count = len(set(colors.values()))
    for _ in range(max(first.number_of_nodes(), second.number_of_nodes())):
        next_signatures: dict[tuple[int, int], tuple[Any, ...]] = {}
        for graph_index, graph in enumerate(graphs):
            for node in graph.nodes():
                neighbors = []
                for neighbor in graph.neighbors(node):
                    edge_label = graph.edge_attr(node, neighbor, "portal") if attributed else ""
                    neighbors.append((edge_label, colors[(graph_index, neighbor)]))
                next_signatures[(graph_index, node)] = (
                    colors[(graph_index, node)],
                    tuple(sorted(neighbors)),
                )
        colors = compact(next_signatures)
        class_count = len(set(colors.values()))
        if class_count == previous_class_count:
            break
        previous_class_count = class_count
    return (
        {node: colors[(0, node)] for node in first.nodes()},
        {node: colors[(1, node)] for node in second.nodes()},
    )


def exact_isomorphic(
    first: GraphData, second: GraphData, attributed: bool = False, state_budget: int = 2_000_000
) -> bool:
    """Exact bounded backtracking after joint 1-WL color refinement.

    The bounded state count is fail-closed: exhausting it raises instead of
    silently classifying a pair as unique.
    """
    if first.number_of_nodes() != second.number_of_nodes() or first.number_of_edges() != second.number_of_edges():
        return False
    first_colors, second_colors = joint_color_refinement(first, second, attributed)
    if collections.Counter(first_colors.values()) != collections.Counter(second_colors.values()):
        return False

    by_color: dict[int, list[int]] = collections.defaultdict(list)
    for node, color in second_colors.items():
        by_color[color].append(node)
    for values in by_color.values():
        values.sort()

    mapping: dict[int, int] = {}
    used: set[int] = set()
    states = 0

    def compatible(left: int, right: int) -> bool:
        for mapped_left, mapped_right in mapping.items():
            left_edge = first.has_edge(left, mapped_left)
            right_edge = second.has_edge(right, mapped_right)
            if left_edge != right_edge:
                return False
            if left_edge and attributed:
                if first.edge_attr(left, mapped_left, "portal") != second.edge_attr(right, mapped_right, "portal"):
                    return False
        return True

    def search() -> bool:
        nonlocal states
        states += 1
        if states > state_budget:
            raise RuntimeError(f"Exact isomorphism state budget exhausted ({state_budget})")
        if len(mapping) == first.number_of_nodes():
            return True

        best_left = -1
        best_candidates: list[int] | None = None
        for left in first.nodes():
            if left in mapping:
                continue
            candidates = [
                right
                for right in by_color[first_colors[left]]
                if right not in used and compatible(left, right)
            ]
            if not candidates:
                return False
            if best_candidates is None or len(candidates) < len(best_candidates):
                best_left = left
                best_candidates = candidates
                if len(candidates) == 1:
                    break
        assert best_candidates is not None
        best_candidates.sort(key=lambda node: (-second.degree(node), node))
        for right in best_candidates:
            mapping[best_left] = right
            used.add(right)
            if search():
                return True
            used.remove(right)
            del mapping[best_left]
        return False

    return search()


def size_multiset(data: VariantData) -> collections.Counter[tuple[float, float]]:
    return collections.Counter(tuple(sorted(size)) for size in data.room_sizes)


def feature_vector(data: VariantData) -> tuple[int, int, int]:
    report = data.validation
    return (
        int(report["interiorSplitWallSegments"]),
        int(report["nativeLowDividerModules"]),
        int(report["nativeOfficePartitionModules"]),
    )


def normalized_vector_distance(first: Iterable[int], second: Iterable[int]) -> float:
    left = list(first)
    right = list(second)
    denominator = sum(max(a, b) for a, b in zip(left, right))
    return sum(abs(a - b) for a, b in zip(left, right)) / denominator if denominator else 0.0


def footprint_distance(first: VariantData, second: VariantData) -> float:
    left = sorted(
        (
            float(first.validation["packedFootprintWidthMeters"]),
            float(first.validation["packedFootprintDepthMeters"]),
        )
    )
    right = sorted(
        (
            float(second.validation["packedFootprintWidthMeters"]),
            float(second.validation["packedFootprintDepthMeters"]),
        )
    )
    return (abs(left[0] - right[0]) + abs(left[1] - right[1])) / (max(left[0], right[0]) + max(left[1], right[1]))


def shortest_distance_histogram(graph: GraphData, root: int = 0) -> collections.Counter[int]:
    distances = {root: 0}
    queue = collections.deque([root])
    while queue:
        node = queue.popleft()
        for neighbor in graph.neighbors(node):
            if neighbor in distances:
                continue
            distances[neighbor] = distances[node] + 1
            queue.append(neighbor)
    if len(distances) != graph.number_of_nodes():
        raise ValueError("Graph is disconnected")
    return collections.Counter(distances.values())


def counter_l1(first: collections.Counter[Any], second: collections.Counter[Any]) -> int:
    return sum(abs(first[key] - second[key]) for key in first.keys() | second.keys())


def triangle_count(graph: GraphData) -> int:
    count = 0
    for first in graph.nodes():
        for second in graph.neighbors(first):
            if second <= first:
                continue
            count += sum(1 for third in graph.neighbors(first) & graph.neighbors(second) if third > second)
    return count


def topology_edit_surrogate(first: GraphData, second: GraphData) -> dict[str, float]:
    """A deterministic invariant diagnostic, not an exact graph edit distance."""
    node_delta = abs(first.number_of_nodes() - second.number_of_nodes())
    edge_delta = abs(first.number_of_edges() - second.number_of_edges())
    degree_delta = counter_l1(
        collections.Counter(first.degree(node) for node in first.nodes()),
        collections.Counter(second.degree(node) for node in second.nodes()),
    ) / 2.0
    safe_distance_delta = counter_l1(
        shortest_distance_histogram(first), shortest_distance_histogram(second)
    ) / 2.0
    triangle_delta = abs(triangle_count(first) - triangle_count(second))
    total = node_delta + edge_delta + degree_delta + safe_distance_delta + triangle_delta
    denominator = max(
        first.number_of_nodes() + first.number_of_edges(),
        second.number_of_nodes() + second.number_of_edges(),
    )
    return {
        "nodeCountDelta": float(node_delta),
        "edgeCountDelta": float(edge_delta),
        "degreeHistogramHalfL1": degree_delta,
        "safeRootDistanceHistogramHalfL1": safe_distance_delta,
        "triangleCountDelta": float(triangle_delta),
        "total": total,
        "normalized": total / denominator if denominator else 0.0,
    }


def load_variants() -> tuple[list[VariantData], dict[str, Any]]:
    matrix_document = json.loads(MATRIX_PATH.read_text(encoding="utf-8"))
    if matrix_document.get("schema") != EXPECTED_MATRIX_SCHEMA:
        raise ValueError(
            f"Expected {EXPECTED_MATRIX_SCHEMA}; found {matrix_document.get('schema')!r}"
        )
    declared_thresholds = matrix_document.get("validationRules", {}).get("layoutUniqueness")
    if declared_thresholds != THRESHOLDS:
        raise ValueError(
            "Matrix layoutUniqueness thresholds differ from the prospective hard floors in the audit tool"
        )
    matrix_by_id = {variant["id"]: variant for variant in matrix_document["variants"]}
    builder = load_builder_definitions()
    if set(builder) != set(matrix_by_id):
        raise ValueError("Builder and matrix variant ID sets differ")

    variants: list[VariantData] = []
    consistency: list[dict[str, Any]] = []
    for validation_path in sorted(VALIDATION_DIR.glob("KH??_validation.json")):
        validation = json.loads(validation_path.read_text(encoding="utf-8"))
        if validation.get("schema") != EXPECTED_SCENE_VALIDATION_SCHEMA:
            raise ValueError(
                f"{validation_path.name} is stale: expected {EXPECTED_SCENE_VALIDATION_SCHEMA}; "
                f"found {validation.get('schema')!r}"
            )
        variant_id = validation["variantId"]
        if variant_id not in matrix_by_id:
            raise ValueError(f"Validation has unknown variant ID: {variant_id}")
        matrix = matrix_by_id[variant_id]
        scene_path = SOURCE_ROOT / "unity_project" / matrix["scenePath"]
        room_types = ["Safe"] + validation["orderedRoomTypeSequence"].split(">")
        room_sizes = [parse_size(value) for value in validation["roomModuleSizesMeters"]]
        room_positions = parse_room_positions(scene_path)
        edges: list[Edge] = []
        for record in validation["portalOffsetMetersByConnection"]:
            match = re.fullmatch(r"(\d{2})_(\d{2})", record["connection"])
            if not match:
                raise ValueError(f"Invalid edge key in {validation_path.name}: {record['connection']}")
            edges.append(
                Edge(
                    int(match.group(1)),
                    int(match.group(2)),
                    record["portal"],
                    record["axis"],
                    float(record["offset"]),
                )
            )

        expected_nodes = int(validation["roomCountIncludingSafe"])
        if len(room_types) != expected_nodes or len(room_sizes) != expected_nodes or len(room_positions) != expected_nodes:
            raise ValueError(
                f"{variant_id}: node evidence mismatch types={len(room_types)} sizes={len(room_sizes)} "
                f"positions={len(room_positions)} expected={expected_nodes}"
            )
        if sorted(room_positions) != list(range(expected_nodes)):
            raise ValueError(f"{variant_id}: room indices are not a complete 0..N-1 set")
        if len(edges) != int(validation["graphConnectionCount"]):
            raise ValueError(f"{variant_id}: edge evidence does not match graphConnectionCount")

        definition = builder[variant_id]
        checks = {
            "matrixVsValidationMoves": matrix["cycleMoves"] == validation["cycleMoves"],
            "matrixVsValidationPortals": matrix["cyclePortalPattern"] == validation["cyclePortalPattern"],
            "matrixVsValidationMotif": matrix["spatialMotif"] == validation["spatialMotif"],
            "builderVsMatrixMoves": definition["moves"] == matrix["cycleMoves"],
            "builderVsMatrixPortals": definition["portals"] == matrix["cyclePortalPattern"],
            "builderVsMatrixMotif": definition["motif"] == matrix["spatialMotif"],
            "scenePathMatchesBuilder": scene_path.stem == definition["sceneName"],
        }
        if not all(checks.values()):
            raise ValueError(f"{variant_id}: builder/matrix/validation mismatch: {checks}")
        consistency.append({"variantId": variant_id, **checks})

        graph = make_graph(room_types, room_sizes, edges, int(validation["baseCycleRoomCountIncludingSafe"]))
        draft = VariantData(
            id=variant_id,
            index=int(validation["variantIndex"]),
            scene_path=scene_path,
            matrix=matrix,
            validation=validation,
            room_types=room_types,
            room_sizes=room_sizes,
            room_positions=room_positions,
            edges=edges,
            graph=graph,
            topology_wl_hash=wl_hash(graph, rounds=6, attributed=False),
            attributed_wl_hash=wl_hash(graph, rounds=6, attributed=True),
            spatial_topology_signature="",
            attributed_spatial_signature="",
            portal_spatial_signature="",
            primary_cycle_shape_signature=canonical_cycle_shape(validation["cycleMoves"]),
        )
        draft.spatial_topology_signature = canonical_spatial_payload(draft, False, False)[0]
        draft.attributed_spatial_signature = canonical_spatial_payload(draft, True, False)[0]
        draft.portal_spatial_signature = canonical_spatial_payload(draft, True, True)[0]
        variants.append(draft)

    if len(variants) != 10:
        raise ValueError(f"Expected ten variants, found {len(variants)}")
    variants.sort(key=lambda variant: variant.index)
    renaming_checks = []
    for variant in variants:
        mapping = {node: variant.graph.number_of_nodes() - 1 - node for node in variant.graph.nodes()}
        permuted = permute_graph(variant.graph, mapping)
        check = {
            "variantId": variant.id,
            "unattributedExactIsomorphismPositiveControl": exact_isomorphic(
                variant.graph, permuted, attributed=False
            ),
            "attributedExactIsomorphismPositiveControl": exact_isomorphic(
                variant.graph, permuted, attributed=True
            ),
            "unattributedWlRenamingInvariant": wl_hash(variant.graph, 6, False)
            == wl_hash(permuted, 6, False),
            "attributedWlRenamingInvariant": wl_hash(variant.graph, 6, True)
            == wl_hash(permuted, 6, True),
        }
        if not all(value for key, value in check.items() if key != "variantId"):
            raise ValueError(f"{variant.id}: graph renaming positive control failed: {check}")
        renaming_checks.append(check)
    return variants, {"checks": consistency, "renamingPositiveControls": renaming_checks, "allPassed": True}


def compare_pair(first: VariantData, second: VariantData) -> dict[str, Any]:
    topology_isomorphic = exact_isomorphic(first.graph, second.graph, attributed=False)
    attributed_isomorphic = exact_isomorphic(first.graph, second.graph, attributed=True)
    topology_similarity = multiset_jaccard(wl_features(first.graph), wl_features(second.graph))
    cycle_similarity = cycle_shape_similarity(
        first.validation["cycleMoves"], second.validation["cycleMoves"]
    )
    first_sequence = first.matrix["roomSequence"]
    second_sequence = second.matrix["roomSequence"]
    room_edits = dihedral_sequence_distance(first_sequence, second_sequence)
    room_distance = room_edits / max(len(first_sequence), len(second_sequence))
    portal_edits = dihedral_hamming(
        first.validation["cyclePortalPattern"], second.validation["cyclePortalPattern"]
    )
    portal_distance = portal_edits / len(first.validation["cyclePortalPattern"])
    footprint_delta = footprint_distance(first, second)
    feature_delta = normalized_vector_distance(feature_vector(first), feature_vector(second))
    size_similarity = multiset_jaccard(size_multiset(first), size_multiset(second))
    loop_rank_delta = abs(int(first.validation["graphLoopRank"]) - int(second.validation["graphLoopRank"]))
    loop_rank_distance = min(loop_rank_delta / 4.0, 1.0)

    distances = {
        "topology": 1.0 - topology_similarity,
        "primaryCycleShape": 1.0 - cycle_similarity,
        "roomTypeSequence": room_distance,
        "cyclePortalPattern": portal_distance,
        "footprint": footprint_delta,
        "roomSizeMultiset": 1.0 - size_similarity,
        "loopRank": loop_rank_distance,
        "spatialFeatureVector": feature_delta,
    }
    weights = {
        "topology": 0.22,
        "primaryCycleShape": 0.14,
        "roomTypeSequence": 0.16,
        "cyclePortalPattern": 0.12,
        "footprint": 0.08,
        "roomSizeMultiset": 0.10,
        "loopRank": 0.08,
        "spatialFeatureVector": 0.10,
    }
    composite_distance = sum(distances[key] * weights[key] for key in weights)

    meaningful_axes = {
        "topology": distances["topology"] >= 0.08,
        "primaryCycleShape": distances["primaryCycleShape"] >= 0.15,
        "roomTypeSequence": room_edits >= 4,
        "cyclePortalPattern": portal_edits >= 3,
        "footprint": footprint_delta >= 0.10,
        "roomSizeMultiset": distances["roomSizeMultiset"] >= 0.15,
        "loopRank": loop_rank_delta >= 2,
        "spatialFeatureVector": feature_delta >= 0.20,
    }
    graph_edit = topology_edit_surrogate(first.graph, second.graph)
    return {
        "pair": [first.id, second.id],
        "exactUnattributedTopologyIsomorphic": topology_isomorphic,
        "exactAttributedTopologyIsomorphic": attributed_isomorphic,
        "exactSpatialTopologyDuplicate": first.spatial_topology_signature == second.spatial_topology_signature,
        "exactAttributedSpatialDuplicate": first.attributed_spatial_signature == second.attributed_spatial_signature,
        "exactPortalSpatialDuplicate": first.portal_spatial_signature == second.portal_spatial_signature,
        "topologyWlSimilarity": round(topology_similarity, 6),
        "topologyEditSurrogate": {key: round(value, 6) for key, value in graph_edit.items()},
        "primaryCycleShapeJaccard": round(cycle_similarity, 6),
        "dihedralRoomSequenceEdits": room_edits,
        "dihedralRoomSequenceDistance": round(room_distance, 6),
        "dihedralCyclePortalEdits": portal_edits,
        "dihedralCyclePortalDistance": round(portal_distance, 6),
        "footprintDistance": round(footprint_delta, 6),
        "roomSizeMultisetJaccard": round(size_similarity, 6),
        "graphLoopRankDelta": loop_rank_delta,
        "spatialFeatureVectorDistance": round(feature_delta, 6),
        "meaningfullyDifferentAxes": [key for key, value in meaningful_axes.items() if value],
        "meaningfullyDifferentAxisCount": sum(meaningful_axes.values()),
        "componentDistances": {key: round(value, 6) for key, value in distances.items()},
        "compositeDistance": round(composite_distance, 6),
        "compositeSimilarity": round(1.0 - composite_distance, 6),
    }


def build_report() -> dict[str, Any]:
    variants, consistency = load_variants()
    pairwise = [compare_pair(first, second) for first, second in itertools.combinations(variants, 2)]
    pairwise.sort(key=lambda record: (-record["compositeSimilarity"], record["pair"]))

    exact_topology = [record for record in pairwise if record["exactUnattributedTopologyIsomorphic"]]
    exact_spatial = [record for record in pairwise if record["exactSpatialTopologyDuplicate"]]
    exact_attributed_spatial = [record for record in pairwise if record["exactAttributedSpatialDuplicate"]]
    most_similar = pairwise[0]
    minimum_room_edits = min(record["dihedralRoomSequenceEdits"] for record in pairwise)
    minimum_portal_edits = min(record["dihedralCyclePortalEdits"] for record in pairwise)
    minimum_axes = min(record["meaningfullyDifferentAxisCount"] for record in pairwise)

    gates = {
        "exactUnattributedTopologyUniqueness": len(exact_topology)
        <= THRESHOLDS["maximumExactUnattributedTopologyIsomorphicPairs"],
        "exactSpatialTopologyUniqueness": len(exact_spatial)
        <= THRESHOLDS["maximumExactSpatialTopologyDuplicatePairs"],
        "exactAttributedSpatialUniqueness": len(exact_attributed_spatial)
        <= THRESHOLDS["maximumExactAttributedSpatialDuplicatePairs"],
        "maximumCompositeSimilarity": most_similar["compositeSimilarity"]
        <= THRESHOLDS["maximumCompositeSimilarity"],
        "minimumMeaningfulAxes": minimum_axes >= THRESHOLDS["minimumMeaningfullyDifferentAxesPerPair"],
        "minimumRoomSequenceEdits": minimum_room_edits >= THRESHOLDS["minimumDihedralRoomSequenceEdits"],
        "minimumCyclePortalEdits": minimum_portal_edits >= THRESHOLDS["minimumDihedralCyclePortalEdits"],
    }
    source_files = [MATRIX_PATH, BUILDER_PATH]
    source_files.extend(sorted(VALIDATION_DIR.glob("KH??_validation.json")))
    source_files.extend(variant.scene_path for variant in variants)

    variant_records = []
    for variant in variants:
        width = float(variant.validation["packedFootprintWidthMeters"])
        depth = float(variant.validation["packedFootprintDepthMeters"])
        variant_records.append(
            {
                "id": variant.id,
                "index": variant.index,
                "scene": variant.scene_path.name,
                "roomCount": variant.graph.number_of_nodes(),
                "connectionCount": variant.graph.number_of_edges(),
                "graphLoopRank": int(variant.validation["graphLoopRank"]),
                "footprintMeters": [width, depth],
                "rotationInvariantAspectRatio": round(max(width, depth) / min(width, depth), 6),
                "spatialMotif": variant.validation["spatialMotif"],
                "spatialFeatureVector": list(feature_vector(variant)),
                "topologyWlHash": variant.topology_wl_hash,
                "attributedWlHash": variant.attributed_wl_hash,
                "spatialTopologySignature": variant.spatial_topology_signature,
                "attributedSpatialSignature": variant.attributed_spatial_signature,
                "portalSpatialSignature": variant.portal_spatial_signature,
                "primaryCycleShapeSignature": variant.primary_cycle_shape_signature,
            }
        )

    return {
        "schema": "vektor-killhouse/layout-uniqueness-audit@1",
        "generatedUtc": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        "scope": "KH01-KH10 current authored Unity scenes and scene-validation@20 records",
        "method": {
            "renamingInvariant": True,
            "translationInvariant": True,
            "rotationInvariant": True,
            "reflectionInvariant": True,
            "unattributedTopology": "stdlib-only exact bounded backtracking after joint color refinement plus six-round Weisfeiler-Lehman hash/features",
            "spatialCanonicalization": "minimum serialized room/edge payload across the eight D4 transforms",
            "sequenceDistance": "minimum Levenshtein distance against direct and reversed safe-room-anchored primary cycle",
            "portalDistance": "minimum Hamming distance against direct and reversed safe-room-anchored primary cycle",
            "graphEditDiagnostic": "deterministic non-gating sum of node/edge-count, degree-histogram, safe-root-distance-histogram, and triangle-count edit terms; this is explicitly not exact GED",
            "compositeWeights": {
                "topology": 0.22,
                "primaryCycleShape": 0.14,
                "roomTypeSequence": 0.16,
                "cyclePortalPattern": 0.12,
                "footprint": 0.08,
                "roomSizeMultiset": 0.10,
                "loopRank": 0.08,
                "spatialFeatureVector": 0.10,
            },
            "excludedFromUniquenessScore": [
                "variant ID",
                "scene name",
                "human-authored topologySignature label",
                "human-authored spatialMotif label",
                "cosmetic prop variant index",
            ],
        },
        "provenance": {
            "sourceFiles": [
                {"path": str(path.relative_to(SOURCE_ROOT)).replace("\\", "/"), "sha256": sha256_path(path)}
                for path in source_files
            ],
            "builderMatrixValidationConsistency": consistency,
        },
        "recommendedHardThresholds": THRESHOLDS,
        "thresholdPolicy": "prospective design floors; portal rhythm is one axis, so one dihedral edit proves non-identity while the composite and multi-axis gates enforce substantive difference",
        "variants": variant_records,
        "pairwise": pairwise,
        "summary": {
            "variantCount": len(variants),
            "pairCount": len(pairwise),
            "uniqueTopologyWlHashCount": len({variant.topology_wl_hash for variant in variants}),
            "uniqueAttributedWlHashCount": len({variant.attributed_wl_hash for variant in variants}),
            "uniqueSpatialTopologySignatureCount": len({variant.spatial_topology_signature for variant in variants}),
            "uniqueAttributedSpatialSignatureCount": len({variant.attributed_spatial_signature for variant in variants}),
            "uniquePortalSpatialSignatureCount": len({variant.portal_spatial_signature for variant in variants}),
            "uniquePrimaryCycleShapeSignatureCount": len({variant.primary_cycle_shape_signature for variant in variants}),
            "exactUnattributedTopologyIsomorphicPairCount": len(exact_topology),
            "exactUnattributedTopologyIsomorphicPairs": [record["pair"] for record in exact_topology],
            "exactSpatialTopologyDuplicatePairCount": len(exact_spatial),
            "exactSpatialTopologyDuplicatePairs": [record["pair"] for record in exact_spatial],
            "exactAttributedSpatialDuplicatePairCount": len(exact_attributed_spatial),
            "exactAttributedSpatialDuplicatePairs": [record["pair"] for record in exact_attributed_spatial],
            "mostSimilarPair": most_similar["pair"],
            "maximumCompositeSimilarity": most_similar["compositeSimilarity"],
            "minimumCompositeDistance": most_similar["compositeDistance"],
            "minimumMeaningfullyDifferentAxisCount": minimum_axes,
            "minimumDihedralRoomSequenceEdits": minimum_room_edits,
            "minimumDihedralCyclePortalEdits": minimum_portal_edits,
            "gates": gates,
            "allPassed": all(gates.values()),
        },
    }


def render_markdown(report: dict[str, Any]) -> str:
    summary = report["summary"]
    lines = [
        "# Kill House layout uniqueness audit",
        "",
        f"Generated: `{report['generatedUtc']}`",
        "",
        "This report compares authored geometry and graph evidence. Variant names and motif labels do not contribute to the score.",
        "",
        "## Outcome",
        "",
        f"- Overall hard-gate result: **{'PASS' if summary['allPassed'] else 'FAIL'}**",
        f"- Unique spatial topology signatures: **{summary['uniqueSpatialTopologySignatureCount']}/10**",
        f"- Unique attributed spatial signatures: **{summary['uniqueAttributedSpatialSignatureCount']}/10**",
        f"- Exact unlabeled topology-isomorphic pairs: **{summary['exactUnattributedTopologyIsomorphicPairCount']}**",
        f"- Unique D4 primary-cycle shapes: **{summary['uniquePrimaryCycleShapeSignatureCount']}/10**",
        f"- Most similar pair: **{' / '.join(summary['mostSimilarPair'])}** at `{summary['maximumCompositeSimilarity']:.3f}` similarity",
        f"- Minimum meaningful-difference axes in any pair: **{summary['minimumMeaningfullyDifferentAxisCount']}**",
        f"- Minimum room-sequence edits: **{summary['minimumDihedralRoomSequenceEdits']}**",
        f"- Minimum cycle-portal edits: **{summary['minimumDihedralCyclePortalEdits']}**",
        "",
        "## Variant structure",
        "",
        "| Variant | Rooms | Connections | Loop rank | Footprint | Aspect | Feature vector |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]
    for variant in report["variants"]:
        lines.append(
            "| {id} | {rooms} | {edges} | {rank} | {width:.0f}x{depth:.0f} m | {aspect:.3f} | {features} |".format(
                id=variant["id"],
                rooms=variant["roomCount"],
                edges=variant["connectionCount"],
                rank=variant["graphLoopRank"],
                width=variant["footprintMeters"][0],
                depth=variant["footprintMeters"][1],
                aspect=variant["rotationInvariantAspectRatio"],
                features="/".join(map(str, variant["spatialFeatureVector"])),
            )
        )

    lines.extend(
        [
            "",
            "Feature vector order is interior split walls / low dividers / office partitions.",
            "",
            "## Closest pairs",
            "",
            "| Pair | Composite similarity | Topology similarity | Edit surrogate | Cycle-shape overlap | Room edits | Portal edits | Different axes |",
            "|---|---:|---:|---:|---:|---:|---:|---:|",
        ]
    )
    for pair in report["pairwise"][:10]:
        graph_edit = pair["topologyEditSurrogate"]["total"]
        lines.append(
            "| {pair} | {composite:.3f} | {topology:.3f} | {edit} | {cycle:.3f} | {room} | {portal} | {axes} |".format(
                pair=" / ".join(pair["pair"]),
                composite=pair["compositeSimilarity"],
                topology=pair["topologyWlSimilarity"],
                edit=f"{graph_edit:.1f}",
                cycle=pair["primaryCycleShapeJaccard"],
                room=pair["dihedralRoomSequenceEdits"],
                portal=pair["dihedralCyclePortalEdits"],
                axes=pair["meaningfullyDifferentAxisCount"],
            )
        )

    lines.extend(["", "## Hard gates", ""])
    for name, passed in summary["gates"].items():
        lines.append(f"- {'PASS' if passed else 'FAIL'}: `{name}`")
    lines.extend(
        [
            "",
            "The edit surrogate is a deterministic non-gating invariant diagnostic, not exact GED. Exact bounded isomorphism and canonical D4 signatures are the authoritative duplicate checks.",
            "",
        ]
    )
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", type=Path, default=DEFAULT_JSON, help="JSON report path")
    parser.add_argument("--markdown", type=Path, default=DEFAULT_MARKDOWN, help="Markdown report path")
    parser.add_argument("--check", action="store_true", help="return non-zero when recommended hard gates fail")
    parser.add_argument("--no-write", action="store_true", help="run gates without rewriting evidence reports")
    args = parser.parse_args()

    report = build_report()
    if not args.no_write:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.markdown.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        args.markdown.write_text(render_markdown(report), encoding="utf-8")
    summary = report["summary"]
    print(
        f"variants={summary['variantCount']} pairs={summary['pairCount']} "
        f"spatialSignatures={summary['uniqueSpatialTopologySignatureCount']} "
        f"exactTopologyIsoPairs={summary['exactUnattributedTopologyIsomorphicPairCount']} "
        f"closest={'/'.join(summary['mostSimilarPair'])} "
        f"similarity={summary['maximumCompositeSimilarity']:.3f} "
        f"passed={summary['allPassed']}"
    )
    return 1 if args.check and not summary["allPassed"] else 0


if __name__ == "__main__":
    sys.exit(main())
