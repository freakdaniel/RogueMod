cmake_minimum_required(VERSION 3.22)

project(RogueModNative LANGUAGES CXX)

set(TARGET RogueModBridge)
add_library(${TARGET} SHARED
    "src/Bridge.cpp"
    "src/UnrealReflectionApi.cpp"
    "include/UnrealReflectionApi.hpp"
    "include/RogueModHostApi.hpp")

target_include_directories(${TARGET} PRIVATE
    "include"
    "${CMAKE_CURRENT_LIST_DIR}/../RogueMod.Sdk/Native/include")
target_compile_features(${TARGET} PRIVATE cxx_std_23)
target_compile_definitions(${TARGET} PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(${TARGET} PRIVATE ole32)
set_target_properties(${TARGET} PROPERTIES OUTPUT_NAME "RogueMod.Bridge")

option(ROGUEMOD_BUILD_SAMPLES "Build RogueMod native samples" ON)
if(ROGUEMOD_BUILD_SAMPLES)
    add_subdirectory("${CMAKE_CURRENT_LIST_DIR}/../RogueMod.Sample.Native" SampleNativeMod)
endif()
