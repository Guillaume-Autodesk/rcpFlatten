#pragma once

#include <algorithm>
#include <cmath>
#include <fstream>
#include <string>
#include <vector>

// Projects points onto a 2D polyline (lines and bulge arcs) and returns the
// station (distance along the polyline) and the signed lateral offset
// (positive on the left of the drawing direction).
//
// Each point is assigned to the segment whose region contains it. The regions
// are delimited at every vertex by the bisector (miter line) of the angle, so
// the plane is partitioned without gaps. Inside its region a point is projected
// on the supporting line (or circle) of its segment WITHOUT clamping, which
// gives the exact lateral offset and avoids piling the points of a corner up on
// the vertex.
//
// The station of a point is its position along its segment, rescaled linearly
// between the two bisector lines so that the bisector line of every vertex maps
// exactly on the station of that vertex.
// The station is therefore continuous across the corners (no gap, no overlap)
// and never drifts away from the station along the polyline: the correction is
// local to each segment and is not accumulated from one corner to the next.
class PolylineProjector
{
public:
    struct Result
    {
        double station;
        double lateral;
    };

    struct Vertex
    {
        double x, y, bulge;
    };

    // Reads the exchange file written by the plug-in:
    //   closed <0|1>
    //   v <x> <y> <bulge>     (one line per vertex)
    bool load(const std::wstring& path, std::string& error)
    {
        std::ifstream in(path);
        if (!in)
        {
            error = "Cannot open polyline file";
            return false;
        }

        bool closed = false;
        std::vector<Vertex> vertices;
        std::string tag;
        while (in >> tag)
        {
            if (tag == "closed")
            {
                int value = 0;
                in >> value;
                closed = value != 0;
            }
            else if (tag == "v")
            {
                Vertex v{};
                in >> v.x >> v.y >> v.bulge;
                vertices.push_back(v);
            }
        }

        if (vertices.size() < 2)
        {
            error = "The polyline needs at least two vertices";
            return false;
        }

        build(vertices, closed);
        if (mSegments.empty())
        {
            error = "The polyline has no usable segment";
            return false;
        }
        return true;
    }

    double totalLength() const { return mLength; }

    // Bounding box (conservative for arcs: full circle) of the polyline.
    void bounds(double& minX, double& minY, double& maxX, double& maxY) const
    {
        minX = minY = 1e300;
        maxX = maxY = -1e300;
        for (const auto& s : mSegments)
        {
            minX = std::min(minX, s.minX);
            minY = std::min(minY, s.minY);
            maxX = std::max(maxX, s.maxX);
            maxY = std::max(maxY, s.maxY);
        }
    }

    // Distance from the polyline within which a point can be kept when the
    // lateral offset is at most maxDistance. It is larger than maxDistance on
    // the outer side of a sharp corner, so use it to extend the bounding box.
    double reach(double maxDistance) const { return maxDistance * mWedge; }

    // Returns false if the point is farther than maxDistance (laterally) from
    // the polyline. Beyond the ends of an open polyline, points are rejected.
    bool project(double px, double py, double maxDistance, Result& result) const
    {
        bool found      = false;
        bool anyRegion  = false;
        double bestDist = maxDistance;

        for (const auto& s : mSegments)
        {
            const double margin = maxDistance * s.wedge;
            if (px < s.minX - margin || px > s.maxX + margin || py < s.minY - margin || py > s.maxY + margin)
                continue;

            double along, lateral;
            if (!projectInRegion(s, px, py, along, lateral))
                continue;

            anyRegion = true;
            const double dist = std::fabs(lateral);
            if (dist > bestDist)
                continue;

            // At this lateral offset the bisector lines cross the segment at
            // lateral * tanStart and at length - lateral * tanEnd (tan = tan(turn / 2)).
            // Map that interval linearly on the station range of the segment.
            const double first = lateral * s.tanStart;
            const double span  = s.length - lateral * (s.tanStart + s.tanEnd);
            const double u     = span > 1e-9 * s.length ? (along - first) / span : along / s.length;
            bestDist           = dist;
            result.lateral     = lateral;
            result.station     = s.cumStart + u * s.length;
            found              = true;
        }

        if (anyRegion)
            return found;

        // Not in the region of any segment (very short segments with a large
        // search distance make the bisector regions overlap): nearest segment.
        return projectNearest(px, py, maxDistance, result);
    }

private:
    struct Segment
    {
        bool isArc;
        double ax, ay, bx, by;
        double cumStart;
        double length;
        bool openStart;    // first segment of an open polyline
        bool openEnd;      // last segment of an open polyline
        // Arc data
        double cx, cy, radius, startAngle, sweep;
        double minX, minY, maxX, maxY;
        // Tangents at the start and at the end of the segment
        double t0x, t0y, t1x, t1y;
        // Unit vectors normal to the bisector lines at the start and end
        // vertices, pointing forward along the polyline
        double sfx, sfy, efx, efy;
        // tan(turn / 2) at the start and end vertices (0 at the ends of an open polyline)
        double tanStart, tanEnd;
        // 1 + largest |tan(turn / 2)| at the two ends: search margin factor
        double wedge;
    };

    static constexpr double Pi      = 3.14159265358979323846;
    static constexpr double TwoPi   = 2.0 * Pi;
    static constexpr double MaxTan  = 20.0;    // caps the effect of near U-turns

    std::vector<Segment> mSegments;
    double mLength = 0.0;
    double mWedge  = 1.0;

    static double normalizeAngle(double a)
    {
        a = std::fmod(a, TwoPi);
        return a < 0.0 ? a + TwoPi : a;
    }

    // Wraps an angle into [-Pi, Pi)
    static double wrapToPi(double a) { return a - TwoPi * std::floor((a + Pi) / TwoPi); }

    // tan(turn / 2) between the end tangent of a segment and the start tangent of the next one
    static double tanHalfTurn(double t1x, double t1y, double t2x, double t2y)
    {
        const double cross = t1x * t2y - t1y * t2x;
        const double dot   = t1x * t2x + t1y * t2y;
        const double denom = 1.0 + dot;
        if (denom < 1e-9)
            return 0.0;
        return std::clamp(cross / denom, -MaxTan, MaxTan);
    }

    // Unit vector along the sum of two unit tangents (normal to the bisector line)
    static void forwardVector(double t1x, double t1y, double t2x, double t2y, double& fx, double& fy)
    {
        const double sx = t1x + t2x;
        const double sy = t1y + t2y;
        const double n  = std::hypot(sx, sy);
        if (n < 1e-9)
        {
            fx = t1x;
            fy = t1y;
        }
        else
        {
            fx = sx / n;
            fy = sy / n;
        }
    }

    void build(const std::vector<Vertex>& v, bool closed)
    {
        const size_t count = v.size();
        const size_t n     = closed ? count : count - 1;
        double cum         = 0.0;

        for (size_t i = 0; i < n; ++i)
        {
            const Vertex& a = v[i];
            const Vertex& b = v[(i + 1) % count];

            Segment s{};
            s.ax = a.x;
            s.ay = a.y;
            s.bx = b.x;
            s.by = b.y;

            const double dx    = b.x - a.x;
            const double dy    = b.y - a.y;
            const double chord = std::hypot(dx, dy);
            if (chord < 1e-12)
                continue;    // skip zero-length segments

            s.cumStart = cum;
            if (std::fabs(a.bulge) < 1e-12)
            {
                s.isArc  = false;
                s.length = chord;
                s.minX   = std::min(a.x, b.x);
                s.maxX   = std::max(a.x, b.x);
                s.minY   = std::min(a.y, b.y);
                s.maxY   = std::max(a.y, b.y);
                s.t0x = s.t1x = dx / chord;
                s.t0y = s.t1y = dy / chord;
            }
            else
            {
                s.isArc        = true;
                s.sweep        = 4.0 * std::atan(a.bulge);
                s.radius       = chord / (2.0 * std::sin(std::fabs(s.sweep) / 2.0));
                const double h = (chord / 2.0) / std::tan(s.sweep / 2.0);
                s.cx           = (a.x + b.x) / 2.0 + (-dy / chord) * h;
                s.cy           = (a.y + b.y) / 2.0 + (dx / chord) * h;
                s.startAngle   = std::atan2(a.y - s.cy, a.x - s.cx);
                s.length       = s.radius * std::fabs(s.sweep);
                s.minX         = s.cx - s.radius;
                s.maxX         = s.cx + s.radius;
                s.minY         = s.cy - s.radius;
                s.maxY         = s.cy + s.radius;

                const double ccw = s.sweep > 0.0 ? 1.0 : -1.0;
                const double end = s.startAngle + s.sweep;
                s.t0x            = -ccw * std::sin(s.startAngle);
                s.t0y            = ccw * std::cos(s.startAngle);
                s.t1x            = -ccw * std::sin(end);
                s.t1y            = ccw * std::cos(end);
            }
            cum += s.length;
            mSegments.push_back(s);
        }

        mLength = cum;
        if (mSegments.empty())
            return;

        // Bisector lines, corner angles and open ends
        const size_t m = mSegments.size();
        mWedge         = 1.0;
        for (size_t k = 0; k < m; ++k)
        {
            Segment& s         = mSegments[k];
            const bool hasPrev = k > 0 || closed;
            const bool hasNext = k + 1 < m || closed;
            double tanStart    = 0.0;
            double tanEnd      = 0.0;

            if (hasPrev)
            {
                const Segment& p = mSegments[(k + m - 1) % m];
                forwardVector(p.t1x, p.t1y, s.t0x, s.t0y, s.sfx, s.sfy);
                tanStart = tanHalfTurn(p.t1x, p.t1y, s.t0x, s.t0y);
            }
            else
            {
                s.sfx       = s.t0x;
                s.sfy       = s.t0y;
                s.openStart = true;
            }

            if (hasNext)
            {
                const Segment& nx = mSegments[(k + 1) % m];
                forwardVector(s.t1x, s.t1y, nx.t0x, nx.t0y, s.efx, s.efy);
                tanEnd = tanHalfTurn(s.t1x, s.t1y, nx.t0x, nx.t0y);
            }
            else
            {
                s.efx     = s.t1x;
                s.efy     = s.t1y;
                s.openEnd = true;
            }

            s.tanStart = tanStart;
            s.tanEnd   = tanEnd;
            s.wedge    = 1.0 + std::max(std::fabs(tanStart), std::fabs(tanEnd));
            mWedge  = std::max(mWedge, s.wedge);
        }
    }

    // Projection of a point on the supporting line or circle of a segment,
    // without clamping. Returns false when the point is not in the region of the
    // segment (the area between the bisector lines of its two end vertices).
    // along: distance from the start of the segment; lateral: signed offset.
    static bool projectInRegion(const Segment& s, double px, double py, double& along, double& lateral)
    {
        const double rx = px - s.ax;
        const double ry = py - s.ay;
        const double ex = px - s.bx;
        const double ey = py - s.by;

        if (rx * s.sfx + ry * s.sfy < 0.0)
            return false;
        const double fe = ex * s.efx + ey * s.efy;
        if (s.openEnd ? fe > 0.0 : fe >= 0.0)
            return false;

        if (!s.isArc)
        {
            along   = rx * s.t0x + ry * s.t0y;
            lateral = -s.t0y * rx + s.t0x * ry;    // left normal of the tangent is (-ty, tx)
            return true;
        }

        const double ccw  = s.sweep > 0.0 ? 1.0 : -1.0;
        const double absS = std::fabs(s.sweep);
        const double dx   = px - s.cx;
        const double dy   = py - s.cy;
        const double ang  = std::atan2(dy, dx);

        // Angular travel from the arc start in the arc direction, unclamped but
        // taken around the middle of the arc so that it stays continuous
        const double du = absS / 2.0 + wrapToPi(ccw * (ang - s.startAngle) - absS / 2.0);

        // Open ends cut exactly at the end of the arc; elsewhere the bisector
        // half-plane above applies, the angle only discards the far side
        if (s.openStart ? du < 0.0 : du < -Pi / 2.0)
            return false;
        if (s.openEnd ? du > absS : du > absS + Pi / 2.0)
            return false;

        along   = s.radius * du;
        lateral = ccw * (s.radius - std::hypot(dx, dy));    // the center is on the left of a counter-clockwise arc
        return true;
    }

    // Nearest segment with the projection clamped inside it (fallback).
    bool projectNearest(double px, double py, double maxDistance, Result& result) const
    {
        double bestDist = maxDistance;
        bool found      = false;

        for (const auto& s : mSegments)
        {
            if (px < s.minX - maxDistance || px > s.maxX + maxDistance || py < s.minY - maxDistance || py > s.maxY + maxDistance)
                continue;

            double dist, station, tx, ty, qx, qy;
            if (!(s.isArc ? nearestOnArc(s, px, py, dist, station, tx, ty, qx, qy) : nearestOnLine(s, px, py, dist, station, tx, ty, qx, qy)))
                continue;

            if (dist > bestDist)
                continue;

            // Left normal of the tangent is (-ty, tx)
            const double side = (-ty) * (px - qx) + tx * (py - qy);
            bestDist          = dist;
            result.station    = station;
            result.lateral    = side >= 0.0 ? dist : -dist;
            found             = true;
        }
        return found;
    }

    static bool nearestOnLine(const Segment& s, double px, double py, double& dist, double& station, double& tx, double& ty, double& qx, double& qy)
    {
        const double dx = s.bx - s.ax;
        const double dy = s.by - s.ay;
        double t        = ((px - s.ax) * dx + (py - s.ay) * dy) / (s.length * s.length);

        if ((t < 0.0 && s.openStart) || (t > 1.0 && s.openEnd))
            return false;

        t       = std::clamp(t, 0.0, 1.0);
        qx      = s.ax + t * dx;
        qy      = s.ay + t * dy;
        tx      = dx / s.length;
        ty      = dy / s.length;
        dist    = std::hypot(px - qx, py - qy);
        station = s.cumStart + t * s.length;
        return true;
    }

    static bool nearestOnArc(const Segment& s, double px, double py, double& dist, double& station, double& tx, double& ty, double& qx, double& qy)
    {
        const double ccw  = s.sweep > 0.0 ? 1.0 : -1.0;
        const double absS = std::fabs(s.sweep);
        const double ang  = std::atan2(py - s.cy, px - s.cx);

        // Angular travel from the arc start, in the arc direction
        double du = normalizeAngle(ccw * (ang - s.startAngle));
        double phi;    // angle of the closest point
        if (du <= absS)
        {
            phi = ang;
        }
        else
        {
            // Outside the arc: the closest end is the one angularly nearest
            const bool nearerEnd = (du - absS) < (TwoPi - du);
            if ((nearerEnd && s.openEnd) || (!nearerEnd && s.openStart))
                return false;
            du  = nearerEnd ? absS : 0.0;
            phi = s.startAngle + ccw * du;
        }

        qx      = s.cx + s.radius * std::cos(phi);
        qy      = s.cy + s.radius * std::sin(phi);
        tx      = -ccw * std::sin(phi);
        ty      = ccw * std::cos(phi);
        dist    = std::hypot(px - qx, py - qy);
        station = s.cumStart + s.radius * du;
        return true;
    }
};
