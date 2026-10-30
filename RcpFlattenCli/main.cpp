// RcpFlattenCli
//
// Unfolds the part of a ReCap point cloud (RCP) located along a 2D polyline
// into a new RCP:
//   X = station along the polyline, Y = original Z, Z = signed lateral offset.
//
// Output protocol (stdout, one line each, flushed):
//   PROGRESS <processed> <total>
//   RESULT <kept points>
//   ERROR <message>
// Exit codes: 0 = success, 2 = no point found in the search zone, 1 = error.

#include <tchar.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <iostream>
#include <string>
#include <vector>

#include <data/IRCPointIterator.h>
#include <data/RCPointBuffer.h>
#include <data/RCPointIteratorSettings.h>
#include <data/RCProject.h>
#include <data/RCProjectImportSession.h>
#include <data/RCScan.h>
#include <data/RCSpatialFilter.h>
#include <foundation/RCFilesystem.h>
#include <foundation/RCTransform.h>

#include "PolylineProjector.h"

using namespace Autodesk::RealityComputing::Foundation;
using namespace Autodesk::RealityComputing::Data;

namespace {

    constexpr int ExitOk      = 0;
    constexpr int ExitError   = 1;
    constexpr int ExitNoPoint = 2;

    constexpr size_t BatchSize = 500000;

    struct Options
    {
        std::wstring input;
        std::wstring output;
        std::wstring polyline;
        double left  = 0.0;
        double right = 0.0;
    };

    int fail(const std::string& message)
    {
        std::printf("ERROR %s\n", message.c_str());
        std::fflush(stdout);
        return ExitError;
    }

    bool parseOptions(int argc, wchar_t* argv[], Options& options)
    {
        for (int i = 1; i + 1 < argc; i += 2)
        {
            const std::wstring key = argv[i];
            const std::wstring val = argv[i + 1];
            if (key == L"--in")
                options.input = val;
            else if (key == L"--out")
                options.output = val;
            else if (key == L"--poly")
                options.polyline = val;
            else if (key == L"--left")
                options.left = std::wcstod(val.c_str(), nullptr);
            else if (key == L"--right")
                options.right = std::wcstod(val.c_str(), nullptr);
            else
                return false;
        }
        return !options.input.empty() && !options.output.empty() && !options.polyline.empty() && options.left >= 0.0 && options.right >= 0.0 &&
               (options.left + options.right) > 0.0;
    }

    // Points accumulated before being pushed to the import session
    struct PointBatch
    {
        std::vector<RCVector3d> positions;
        std::vector<RCVector4ub> colors;
        std::vector<unsigned char> intensities;
        std::vector<unsigned char> classifications;

        size_t size() const { return positions.size(); }

        void clear()
        {
            positions.clear();
            colors.clear();
            intensities.clear();
            classifications.clear();
        }
    };

    bool flush(RCProjectImportSession& session, PointBatch& batch, bool withColor, bool withIntensity, bool withClassification)
    {
        if (batch.size() == 0)
            return true;

        RCPointBuffer buffer;
        if (withColor)
            buffer.addAttribute(RCAttributeType::Color);
        if (withIntensity)
            buffer.addAttribute(RCAttributeType::Intensity);
        if (withClassification)
            buffer.addAttribute(RCAttributeType::Classification);
        buffer.resize(static_cast<unsigned int>(batch.size()));

        for (unsigned int i = 0; i < batch.size(); ++i)
        {
            buffer.setPositionAt(i, batch.positions[i]);
            if (withColor)
                buffer.setColorAt(i, batch.colors[i]);
            if (withIntensity)
                buffer.setIntensityAt(i, batch.intensities[i]);
            if (withClassification)
                buffer.setClassificationAt(i, batch.classifications[i]);
        }

        const auto code = session.addPointsToScan(buffer);
        batch.clear();
        return code == RCCode::OK;
    }

}    // namespace

int wmain(int argc, wchar_t* argv[])
{
    // Diagnostic mode: RcpFlattenCli --info <file.rcp>
    if (argc == 3 && std::wstring(argv[1]) == L"--info")
    {
        RCCode infoCode;
        auto info = RCProject::loadFromFile(RCString(argv[2]), RCFileAccess::ReadOnly, RCProjectUserEdits::None, infoCode);
        if (info == nullptr)
            return fail("Cannot open the RCP (code " + std::to_string(static_cast<int>(infoCode)) + ")");
        const auto box = info->getBoundingBox();
        std::printf("POINTS %llu SCANS %d\n", info->getNumberOfPoints(), info->getNumberOfScans());
        std::printf("MIN %f %f %f\nMAX %f %f %f\n", box.getMin().x, box.getMin().y, box.getMin().z, box.getMax().x, box.getMax().y, box.getMax().z);
        return ExitOk;
    }

    Options options;
    if (!parseOptions(argc, argv, options))
        return fail("Usage: RcpFlattenCli --in <src.rcp> --out <dst.rcp> --poly <file> --left <d> --right <d>");

    PolylineProjector projector;
    std::string error;
    if (!projector.load(options.polyline, error))
        return fail(error);

    RCCode code = RCCode::OK;
    auto project = RCProject::loadFromFile(RCString(options.input.c_str()), RCFileAccess::ReadOnly, RCProjectUserEdits::None, code);
    if (project == nullptr)
        return fail("Cannot open the source RCP (code " + std::to_string(static_cast<int>(code)) + ")");

    // Attributes available in the source
    bool hasColor = false, hasIntensity = false;
    for (int i = 0; i < project->getNumberOfScans(); ++i)
    {
        const auto scan = project->getScanAt(i);
        hasColor |= scan->hasColors();
        hasIntensity |= scan->hasIntensities();
    }

    // Coarse spatial pre-filter: polyline bounds extended by the buffers
    const double maxOffset = std::max(options.left, options.right);
    double minX, minY, maxX, maxY;
    projector.bounds(minX, minY, maxX, maxY);
    const auto projectBox = project->getBoundingBox();
    const RCVector3d boxMin(minX - maxOffset, minY - maxOffset, projectBox.getMin().z - 1.0);
    const RCVector3d boxMax(maxX + maxOffset, maxY + maxOffset, projectBox.getMax().z + 1.0);
    project->addTemporarySpatialFilter(RCSharedPtr<RCSpatialFilter>(new RCBoxSpatialFilter(boxMin, boxMax)));

    const uint64_t totalPoints = project->getNumberOfPoints();

    RCPointIteratorSettings settings;
    settings.setDensity(-1.0);
    settings.setIsVisiblePointsOnly(false);
    auto iterator = project->createPointIterator(settings);

    // The output project is created lazily on the first kept point, so no file
    // is left behind when nothing is found in the search zone.
    RCSharedPtr<RCProjectImportSession> session;
    PointBatch batch;
    bool withClassification = false;
    uint64_t processed      = 0;
    uint64_t kept           = 0;
    double lo[3] = {1e300, 1e300, 1e300}, hi[3] = {-1e300, -1e300, -1e300};
    double srcLo[3] = {1e300, 1e300, 1e300}, srcHi[3] = {-1e300, -1e300, -1e300};

    while (iterator->moveToNextPoint())
    {
        ++processed;
        if (processed % 1000000 == 0)
        {
            std::printf("PROGRESS %llu %llu\n", processed, totalPoints);
            std::fflush(stdout);
        }

        const auto& point = iterator->getPoint();
        const auto& pos   = point.getPosition();

        const double src[3] = {pos.x, pos.y, pos.z};
        for (int k = 0; k < 3; ++k)
        {
            srcLo[k] = std::min(srcLo[k], src[k]);
            srcHi[k] = std::max(srcHi[k], src[k]);
        }

        PolylineProjector::Result r;
        if (!projector.project(pos.x, pos.y, maxOffset, r))
            continue;
        if (r.lateral > options.left || -r.lateral > options.right)
            continue;

        if (session == nullptr)
        {
            withClassification = point.hasClassification();

            session = RCProjectImportSession::init(RCString(options.output.c_str()), RCFileMode::New, code);
            if (session == nullptr || code != RCCode::OK)
                return fail("Cannot create the output RCP (code " + std::to_string(static_cast<int>(code)) + ")");

            RCScanMetadata metadata;
            metadata.name                    = L"Flattened";
            metadata.provider                = RCScanProvider::ReCap;
            metadata.coordinateSystem        = L"";
            metadata.verticalCoordinateSystem = L"";
            metadata.isStructured            = false;
            metadata.estimatedPointCount     = totalPoints;
            RCTransform identity;
            identity.identity();
            identity.toRowMajor(metadata.globalTransformation);

            // Keep the points as they are: no noise removal, no intensity stretching
            RCScanImportOptions importOptions;
            importOptions.noiseFilterLevel  = RCNoiseFilterLevel::None;
            importOptions.normalizeIntensity = false;

            code = session->addScan(metadata, importOptions);
            if (code != RCCode::OK)
                return fail("addScan failed (code " + std::to_string(static_cast<int>(code)) + ")");
        }

        batch.positions.emplace_back(r.station, pos.z, r.lateral);
        if (hasColor)
            batch.colors.push_back(point.getColor());
        if (hasIntensity)
            batch.intensities.push_back(point.getNormalizedIntensity());
        if (withClassification)
            batch.classifications.push_back(point.getClassification());
        ++kept;
        const double out[3] = {r.station, pos.z, r.lateral};
        for (int k = 0; k < 3; ++k)
        {
            lo[k] = std::min(lo[k], out[k]);
            hi[k] = std::max(hi[k], out[k]);
        }

        if (batch.size() >= BatchSize && !flush(*session, batch, hasColor, hasIntensity, withClassification))
            return fail("addPointsToScan failed");
    }
    iterator->close();

    if (session == nullptr)
    {
        std::printf("RESULT 0\n");
        std::fflush(stdout);
        return ExitNoPoint;
    }

    if (!flush(*session, batch, hasColor, hasIntensity, withClassification))
        return fail("addPointsToScan failed");

    session->processScan();
    session->endSession();
    session->waitTillFinished();

    std::printf("RANGE_IN %f %f %f %f %f %f\n", srcLo[0], srcLo[1], srcLo[2], srcHi[0], srcHi[1], srcHi[2]);
    std::printf("RANGE_OUT %f %f %f %f %f %f\n", lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]);
    std::printf("RESULT %llu\n", kept);
    std::fflush(stdout);
    return ExitOk;
}
