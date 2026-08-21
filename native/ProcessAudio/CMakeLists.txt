cmake_minimum_required(VERSION 3.20)
project(CaptailProcessAudio LANGUAGES CXX)

if(NOT DEFINED OBS_INCLUDE_DIR OR NOT EXISTS "${OBS_INCLUDE_DIR}/obs-module.h")
  message(FATAL_ERROR "OBS_INCLUDE_DIR must point to the pinned libobs public headers")
endif()

add_library(CaptailProcessAudio MODULE CaptailProcessAudio.cpp)

set(OBS_IMPORT_LIBRARY "${CMAKE_CURRENT_BINARY_DIR}/obs.lib")
add_custom_command(
  OUTPUT "${OBS_IMPORT_LIBRARY}"
  COMMAND
    "${CMAKE_AR}" /nologo
    "/def:${CMAKE_CURRENT_SOURCE_DIR}/Obs.def"
    /machine:x64
    "/out:${OBS_IMPORT_LIBRARY}"
  DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/Obs.def"
  VERBATIM)
add_custom_target(CaptailObsImportLibrary DEPENDS "${OBS_IMPORT_LIBRARY}")
add_dependencies(CaptailProcessAudio CaptailObsImportLibrary)

target_compile_features(CaptailProcessAudio PRIVATE cxx_std_20)
target_compile_definitions(
  CaptailProcessAudio
  PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE _WIN32_WINNT=0x0A00)
target_include_directories(CaptailProcessAudio SYSTEM PRIVATE "${OBS_INCLUDE_DIR}")
target_link_libraries(CaptailProcessAudio PRIVATE "${OBS_IMPORT_LIBRARY}" avrt ole32)

if(MSVC)
  target_compile_options(CaptailProcessAudio PRIVATE /W4 /permissive- /EHsc)
  set_property(
    TARGET CaptailProcessAudio
    PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
endif()

set_target_properties(
  CaptailProcessAudio
  PROPERTIES OUTPUT_NAME "captail-process-audio" PREFIX "")
