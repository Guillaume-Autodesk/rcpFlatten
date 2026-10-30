#pragma once

#include <algorithm>
#include <cmath>
#include <fstream>
#include <string>
#include <vector>

// Projects points onto a 2D polyline (lines and bulge arcs) and returns the
// station (distance along the polyline) and the signed lateral offset
// (positive on the left of the drawing direction).
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

    // Returns false if the point is farther than maxDistance from every valid
    // segment. Beyond the ends of an open polyline, points are rejected.
    bool project(double px, double py, double maxDistance, Result& result) const
    {
        double bestDist = maxDistance;
        bool found      = false;

        for (const auto& s : mSegments)
        {
            if (px < s.minX - maxDistance || px > s.maxX + maxDistance || py < s.minY - maxDistance || py > s.maxY + maxDistance)
                continue;

            double dist, station, tx, ty, qx, qy;
            if (!(s.isArc ? projectOnArc(s, px, py, dist, station, tx, ty, qx, qy) : projectOnLine(s, px, py, dist, station, tx, ty, qx, qy)))
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
    };

    static constexpr double Pi    = 3.14159265358979323846;
    static constexpr double TwoPi = 2.0 * Pi;

    std::vector<Segment> mSegments;
    double mLength = 0.0;

    static double normalizeAngle(double a)
    {
        a = std::fmod(a, TwoPi);
        return a < 0.0 ? a + TwoPi : a;
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
            }
            cum += s.length;
            mSegments.push_back(s);
        }

        mLength = cum;
        if (!mSegments.empty() && !closed)
        {
            mSegments.front().openStart = true;
            mSegments.back().openEnd    = true;
        }
    }

    static bool projectOnLine(const Segment& s, double px, double py, double& dist, double& station, double& tx, double& ty, double& qx, double& qy)
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

    static bool projectOnArc(const Segment& s, double px, double py, double& dist, double& station, double& tx, double& ty, double& qx, double& qy)
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
